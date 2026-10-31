//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Threading;

using Antmicro.Renode.Time;
using Antmicro.Renode.UnitTests.Utilities;

using NUnit.Framework;

namespace UnitTests
{
    [TestFixture]
    public class TimeHandleTests
    {
        [SetUp]
        public void SetUp()
        {
            tsource = new MockTimeSource();
            var mockSink = new MockTimeSink();
            handle = new TimeHandle(tsource, mockSink);
            mockSink.TimeHandle = handle;
            tester = new ThreadSyncTester();

            sourceThread = tester.ObtainThread("source");
            sinkThread = tester.ObtainThread("sink");
            externalThread = tester.ObtainThread("external");

            interval = TimeInterval.FromTicks(1000);

            SetSourceSideActiveOnExternal(true);
        }

        [TearDown]
        public void TearDown()
        {
            Assert.IsTrue(tester.ExecutionFinished, "Did you forget to call `Finish`?");
            tester.Dispose();
        }

        [Test]
        public void ShouldHandleProgressIncludingResiduum()
        {
            GrantOnSource();

            RequestOnSink().ShouldFinish();
            ContinueOnSink(TimeInterval.FromTicks(100));

            WaitOnSource().ShouldFinish();
            GrantOnSource();

            RequestOnSink().ShouldFinish();
            // report progress: a granted quantum + residuum from the previous run
            ReportProgressOnSink(TimeInterval.FromTicks(1100));
            ContinueOnSink();

            WaitOnSource().ShouldFinish();
            Finish();
        }

        [Test]
        public void ShouldConserveCarriedCreditAcrossRepeatedBreaks(
            [Values(0, 50, 100, 150, 500, 1100)] int consumed,
            [Values(false, true)] bool reportProgress)
        {
            try
            {
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                ReportProgressOnSink(TimeInterval.FromTicks(900));
                ContinueOnSink(TimeInterval.FromTicks(100));
                WaitOnSource().ShouldFinish();
                ShouldWaitReturn(true, false, interval);
                Assert.AreEqual(1000ul, handle.TotalElapsedTime.Ticks);

                GrantOnSource();
                RequestOnSink().ShouldFinish();
                ShouldRequestReturn(true, TimeInterval.FromTicks(1100));
                if(reportProgress) ReportProgressOnSink(TimeInterval.FromTicks((ulong)consumed));
                var remaining = TimeInterval.FromTicks((ulong)(1100 - consumed));
                var currentGrantUsed = (ulong)Math.Max(0, consumed - 100);
                BreakOnSink(remaining);
                sinkThread.CheckException();
                WaitOnSource().ShouldFinish();
                ShouldWaitReturn(false, false, TimeInterval.FromTicks(currentGrantUsed));
                Assert.AreEqual(1000ul + currentGrantUsed, handle.TotalElapsedTime.Ticks);

                for(var resume = 0; resume < 3; resume++)
                {
                    var request = RequestOnSink();
                    Assert.IsTrue(SpinWait.SpinUntil(() => handle.IsReadyToBeUnblocked, TimeSpan.FromSeconds(5)),
                        "The resumed request did not wait for the source to unblock it");
                    UnblockOnSource();
                    request.ShouldFinish();
                    ShouldRequestReturn(true, remaining);
                    if(resume != 2)
                    {
                        BreakOnSink(remaining);
                        sinkThread.CheckException();
                        WaitOnSource().ShouldFinish();
                        ShouldWaitReturn(false, true, TimeInterval.Empty);
                        Assert.AreEqual(1000ul + currentGrantUsed, handle.TotalElapsedTime.Ticks);
                    }
                    else
                    {
                        ReportProgressOnSink(remaining);
                        ContinueOnSink();
                        WaitOnSource().ShouldFinish();
                        ShouldWaitReturn(true, true, TimeInterval.FromTicks(1000ul - currentGrantUsed));
                    }
                }
                Assert.AreEqual(2000ul, handle.TotalElapsedTime.Ticks, "Carried time must not be lost or counted twice");
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                ShouldRequestReturn(true, interval);
                ReportProgressOnSink(interval);
                ContinueOnSink();
                WaitOnSource().ShouldFinish();
                Assert.AreEqual(3000ul, handle.TotalElapsedTime.Ticks);
            }
            finally { Finish(); }
        }

        [Test]
        public void ShouldRejectLeftoverBeyondOutstandingCreditWithoutChangingState([Values(0, 50, 150)] int consumed)
        {
            try
            {
                AccumulatePreviouslyReportedCredit();
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                ReportProgressOnSink(TimeInterval.FromTicks((ulong)consumed));
                var remaining = TimeInterval.FromTicks((ulong)(1100 - consumed));
                tester.Execute(sinkThread, () =>
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() =>
                        handle.ReportBackAndBreak(remaining + TimeInterval.FromTicks(1)));
                    return null;
                }, "Reject excessive leftover").ShouldFinish();
                sinkThread.CheckException();
                BreakOnSink(remaining);
                WaitOnSource().ShouldFinish();
                var request = RequestOnSink();
                Assert.IsTrue(SpinWait.SpinUntil(() => handle.IsReadyToBeUnblocked, TimeSpan.FromSeconds(5)));
                UnblockOnSource();
                request.ShouldFinish();
                ShouldRequestReturn(true, remaining);
                ReportProgressOnSink(remaining);
                ContinueOnSink();
                WaitOnSource().ShouldFinish();
                Assert.AreEqual(2000ul, handle.TotalElapsedTime.Ticks);
            }
            finally { Finish(); }
        }

        [Test]
        public void ShouldDiscardBlockedCreditWhenDisabledWithoutDuplicatingLaterTime()
        {
            try
            {
                AccumulatePreviouslyReportedCredit();
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                BreakOnSink(TimeInterval.FromTicks(1100));
                WaitOnSource().ShouldFinish();
                SetEnabledOnExternal(false).ShouldFinish();
                WaitOnSource().ShouldFinish();
                ShouldWaitReturn(true, false, interval);
                Assert.AreEqual(2000ul, handle.TotalElapsedTime.Ticks);
                SetEnabledOnExternal(true).ShouldFinish();
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                ShouldRequestReturn(true, interval);
                ReportProgressOnSink(interval);
                ContinueOnSink();
                WaitOnSource().ShouldFinish();
                Assert.AreEqual(3000ul, handle.TotalElapsedTime.Ticks);
            }
            finally { Finish(); }
        }

        [Test]
        public void ShouldDetachWhileBlockedWithCredit([Values(false, true)] bool requestWaiting)
        {
            try
            {
                AccumulatePreviouslyReportedCredit();
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                BreakOnSink(TimeInterval.FromTicks(1100));
                WaitOnSource().ShouldFinish();
                ThreadSyncTester.ExecutionResult request = null;
                if(requestWaiting)
                {
                    request = RequestOnSink();
                    Assert.IsTrue(SpinWait.SpinUntil(() => handle.IsReadyToBeUnblocked, TimeSpan.FromSeconds(5)));
                }
                DisposeOnExternal();
                if(request == null) request = RequestOnSink();
                request.ShouldFinish();
                ShouldRequestReturn(false, TimeInterval.Empty);
                Assert.IsTrue(handle.DetachRequested);
                Assert.IsFalse(handle.IsReadyForNewTimeGrant);
                BreakOnSink(TimeInterval.FromTicks(1100)); // An already detached sink ignores a late report.
                Assert.AreEqual(1000ul, handle.TotalElapsedTime.Ticks);
            }
            finally { Finish(); }
        }

        [TestCase(0)]
        [TestCase(100)]
        public void ShouldHandleZeroGrantsWithOrWithoutCarriedCredit(int credit)
        {
            try
            {
                if(credit != 0) AccumulatePreviouslyReportedCredit();
                var before = handle.TotalElapsedTime;
                interval = TimeInterval.Empty;
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                var carried = TimeInterval.FromTicks((ulong)credit);
                ShouldRequestReturn(true, carried);
                BreakOnSink(carried);
                WaitOnSource().ShouldFinish();
                ShouldWaitReturn(false, false, TimeInterval.Empty);
                var request = RequestOnSink();
                Assert.IsTrue(SpinWait.SpinUntil(() => handle.IsReadyToBeUnblocked, TimeSpan.FromSeconds(5)));
                UnblockOnSource();
                request.ShouldFinish();
                ShouldRequestReturn(true, carried);
                ReportProgressOnSink(carried);
                ContinueOnSink();
                WaitOnSource().ShouldFinish();
                ShouldWaitReturn(true, true, TimeInterval.Empty);
                Assert.AreEqual(before, handle.TotalElapsedTime);
                interval = TimeInterval.FromTicks(1000);
                GrantOnSource();
                RequestOnSink().ShouldFinish();
                ShouldRequestReturn(true, interval);
                ReportProgressOnSink(interval);
                ContinueOnSink();
                WaitOnSource().ShouldFinish();
                Assert.AreEqual(before + interval, handle.TotalElapsedTime);
            }
            finally { Finish(); }
        }

        private void AccumulatePreviouslyReportedCredit()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            ReportProgressOnSink(TimeInterval.FromTicks(900));
            ContinueOnSink(TimeInterval.FromTicks(100));
            WaitOnSource().ShouldFinish();
            ShouldWaitReturn(true, false, interval);
            Assert.AreEqual(1000ul, handle.TotalElapsedTime.Ticks);
        }

        [Test]
        public void WaitShouldNotBlockIfThereWasNoRequest()
        {
            GrantOnSource();
            WaitOnSource().ShouldFinish();
            // we expect 'WaitUntilDone' to finish with false if sink has not interacted with the handle yet
            ShouldWaitReturn(false, false, TimeInterval.Empty);
            Finish();
        }

        [Test]
        public void RequestShouldBlockWaitingForGrant()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            ShouldRequestReturn(true, interval);
            Finish();
        }

        [Test]
        public void WaitShouldBlockWaitingForReport()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            ShouldRequestReturn(true, interval);
            WaitOnSource().ShouldBlock();
            Finish();
        }

        [Test]
        public void WaitShouldBlockWaitingForReport2()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            ShouldRequestReturn(true, interval);
            WaitOnSource().ShouldBlock();
            Finish();
        }

        [Test]
        public void HandleShouldNotBeReadyForNewTimeGrantAfterDispose()
        {
            DisposeOnExternal();
            Assert.IsFalse(handle.IsReadyForNewTimeGrant);
            Finish();
        }

#if DEBUG
        // those tests assume DebugHelper.Assert to throw an exception; asserts are supported in debug mode only, so it would fail in non-debug mode

        [Test]
        public void ShouldNotAllowToGrantTimeAfterDispose()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                DisposeOnExternal();
                GrantOnSource();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToGrantTwiceInARow()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                GrantOnSource();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToWaitBeforeGranting()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                WaitOnSource().ShouldBlock(); // this should block here is a hack not to finish this test too soon
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToGrantTimeToBlockedHandle()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                BreakOnSink();
                WaitOnSource();
                GrantOnSource().ShouldBlock(); // this is illegal; sink must call UnblockHandle (as a result of RequestTimeInterval) first!
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToCallWaitTwiceAfterReportingContinue()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                ContinueOnSink();
                WaitOnSource();
                WaitOnSource().ShouldBlock(); // this should block here is a hack not to finish this test too soon
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToCallGrantAfterReportingBreak()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                BreakOnSink();
                WaitOnSource();
                GrantOnSource();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToRequestTwiceInARow()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                RequestOnSink().ShouldBlock(); // this should block here is a hack not to finish this test too soon
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToReportContinueBeforeRequesting()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                ContinueOnSink();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToReportBreakBeforeRequesting()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                BreakOnSink();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToReportBreakTwiceInARow()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                BreakOnSink();
                BreakOnSink();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToReportContinueTwiceInARow()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                ContinueOnSink();
                ContinueOnSink();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToReportContinueAfterBreak()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                BreakOnSink();
                ContinueOnSink();
                Finish();
            });
        }

        [Test]
        public void ShouldNotAllowToReportBreakAfterContinue()
        {
            Assert.Throws<Antmicro.Renode.Debugging.AssertionException>(() =>
            {
                GrantOnSource();
                RequestOnSink();
                ContinueOnSink();
                BreakOnSink();
                Finish();
            });
        }
#endif

        [Test]
        public void RequestShouldReturnDifferentValuesDependingOnSideEnabledState2()
        {
            var r = RequestOnSink().ShouldBlock();
            SetSourceSideActiveOnExternal(false);
            r.ShouldFinish();
            RequestOnSink().ShouldFinish(Tuple.Create(false, TimeInterval.Empty));
            SetSourceSideActiveOnExternal(true);
            r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            WaitOnSource().ShouldBlock();
            Finish();

            ShouldRequestReturn(true, interval);
        }

        [Test]
        public void GratingShouldFinishRequestAfterReEnablingSourceSide()
        {
            SetSourceSideActiveOnExternal(false);
            SetSourceSideActiveOnExternal(true);
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
        }

        [Test]
        public void DisablingHanldeSinkSideShouldFinishRequest()
        {
            var r = RequestOnSink().ShouldBlock();
            SetEnabledOnExternal(false).ShouldFinish();
            r.ShouldFinish();
            Finish();

            ShouldRequestReturn(false, TimeInterval.Empty);
        }

        [Test]
        public void DisablingHanldeSourceSideShouldFinishRequest()
        {
            var r = RequestOnSink().ShouldBlock();
            SetSourceSideActiveOnExternal(false);
            r.ShouldFinish();
            Finish();

            ShouldRequestReturn(false, TimeInterval.Empty);
        }

        [Test]
        public void RequestShouldNotBlockOnDisabledHandle()
        {
            SetEnabledOnExternal(false).ShouldFinish();
            RequestOnSink().ShouldFinish();
            Finish();

            ShouldRequestReturn(false, TimeInterval.Empty);
        }

        [Test]
        public void BreakingShouldFinishWaiting3()
        {
            GrantOnSource();
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, TimeInterval.Empty));
            var r = RequestOnSink().ShouldBlock();
            UnblockOnSource();
            r.ShouldFinish(Tuple.Create(true, interval));
            var w = WaitOnSource().ShouldBlock();
            BreakOnSink();
            w.ShouldFinish(Tuple.Create(false, true, interval));
            Finish();
        }

        [Test]
        public void BreakingShouldFinishWaiting2()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            var w = WaitOnSource().ShouldBlock();
            BreakOnSink();
            w.ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(false, false, interval);
        }

        [Test]
        public void BreakingShouldFinishWaiting()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            var w = WaitOnSource().ShouldBlock();
            BreakOnSink();
            w.ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(false, false, interval);
        }

        [Test]
        public void ContinueShouldFinishWaiting()
        {
            GrantOnSource();
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, TimeInterval.Empty));
            var r = RequestOnSink().ShouldBlock();
            UnblockOnSource();
            r.ShouldFinish(Tuple.Create(true, interval));
            var w = WaitOnSource().ShouldBlock();
            ContinueOnSink();
            w.ShouldFinish(Tuple.Create(true, true, interval));
            Finish();
        }

        [Test]
        public void ContinueShouldFinishWaiting2()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            var w = WaitOnSource().ShouldBlock();
            ContinueOnSink();
            w.ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(true, false, interval);
        }

        [Test]
        public void ContinueShouldFinishWaiting3()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            var w = WaitOnSource().ShouldBlock();
            ContinueOnSink();
            w.ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(true, false, interval);
        }

        [Test]
        public void WaitShouldNotBlockAfterContinue()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            ContinueOnSink();
            WaitOnSource().ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(true, false, interval);
        }

        [Test]
        public void WaitShouldNotBlockAfterContinue2()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            ContinueOnSink();
            WaitOnSource().ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(true, false, interval);
        }

        [Test]
        public void WaitShouldNotBlockAfterBreak()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            BreakOnSink();
            WaitOnSource().ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(false, false, interval);
        }

        [Test]
        public void WaitShouldNotBlockAfterBreak2()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish();
            BreakOnSink();
            WaitOnSource().ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
            ShouldWaitReturn(false, false, interval);
        }

        [Test]
        public void WaitShouldNotBlockOnDisabledHandle()
        {
            var r = RequestOnSink().ShouldBlock();
            SetEnabledOnExternal(false).ShouldFinish();
            r.ShouldFinish();
            GrantOnSource();
            WaitOnSource().ShouldFinish();
            Finish();

            ShouldRequestReturn(false, TimeInterval.Empty);
            ShouldWaitReturn(true, false, interval);
        }

        [Test]
        public void RequestSholdNotBlockAfterGrantOnReEnabledHandle()
        {
            SetSourceSideActiveOnExternal(false);
            SetSourceSideActiveOnExternal(true);
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            Finish();

            ShouldRequestReturn(true, interval);
        }

        [Test]
        public void RequestAfterBreakShouldBlockWaitingForUnblock()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish(Tuple.Create(true, interval));
            BreakOnSink();
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, interval));
            RequestOnSink().ShouldBlock();
            Finish();
        }

        [Test]
        public void ShouldUnblockHandle()
        {
            var r = RequestOnSink().ShouldBlock();
            GrantOnSource();
            r.ShouldFinish(Tuple.Create(true, interval));
            BreakOnSink();
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, interval));
            r = RequestOnSink().ShouldBlock();
            UnblockOnSource().ShouldFinish();
            r.ShouldFinish(Tuple.Create(true, TimeInterval.Empty));
            Finish();
        }

        [Test]
        public void SecondWaitShouldReturnEmptyIntervalOnBlockingHandle()
        {
            GrantOnSource();
            RequestOnSink().ShouldFinish();
            BreakOnSink();
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, interval));
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, TimeInterval.Empty));
            Finish();
        }

        [Test]
        public void WaitShouldNotBlockAfterGrantingOnReEnabledHandleS()
        {
            SetEnabledOnExternal(false).ShouldFinish();
            SetEnabledOnExternal(true).ShouldFinish();
            GrantOnSource();
            WaitOnSource().ShouldFinish();
            Finish();

            ShouldWaitReturn(false, false, TimeInterval.Empty);
        }

        [Test]
        public void DisableSinkSideShouldBlockOnLatchedHandle()
        {
            var r = RequestOnSink().ShouldBlock();
            SetSourceSideActiveOnExternal(false);
            r.ShouldFinish();
            SetSourceSideActiveOnExternal(true);

            SetEnabledOnExternal(false).ShouldFinish();
            LatchOnSource();
            GrantOnSource();
            var s = SetEnabledOnExternal(true).ShouldBlock();
            WaitOnSource().ShouldFinish();
            UnlatchOnSource();
            s.ShouldFinish();
            Finish();

            ShouldWaitReturn(true, false, interval);
        }

        [Test]
        public void ShouldHandleDisablingBlockedHandle()
        {
            GrantOnSource().ShouldFinish();
            RequestOnSink().ShouldFinish();
            BreakOnSink(TimeInterval.FromTicks(500)).ShouldFinish();
            WaitOnSource().ShouldFinish(Tuple.Create(false, false, TimeInterval.FromTicks(500)));

            // here we disable a blocked handle - it should report back the rest of a quant
            SetEnabledOnExternal(false).ShouldFinish();
            WaitOnSource().ShouldFinish(Tuple.Create(true, false, TimeInterval.FromTicks(500)));

            // now it should automatically report all the quant
            GrantOnSource().ShouldFinish();
            WaitOnSource().ShouldFinish(Tuple.Create(true, false, TimeInterval.FromTicks(1000)));

            // here we re-enable the handle - it should *not remember* that it was blocked before disabling
            SetEnabledOnExternal(true).ShouldFinish();
            GrantOnSource().ShouldFinish();
            RequestOnSink().ShouldFinish();
            ContinueOnSink().ShouldFinish();
            WaitOnSource().ShouldFinish(Tuple.Create(true, false, TimeInterval.FromTicks(1000)));

            Finish();
        }

        private ThreadSyncTester.ExecutionResult GrantOnSource()
        {
            // granting should never block
            return tester.Execute(sourceThread, () => { handle.GrantTimeInterval(interval); return null; }, "Grant").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult UnblockOnSource()
        {
            // unblocking should never block
            return tester.Execute(sourceThread, () => { var r = handle.UnblockHandle(); return r; }, "Unblock").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult WaitOnSource()
        {
            // it may block or not
            waitResult = tester.Execute(sourceThread, () => { var r = handle.WaitUntilDone(out var ti); return Tuple.Create(r.IsDone, r.IsUnblockedRecently, ti); }, "Wait");
            return waitResult;
        }

        private ThreadSyncTester.ExecutionResult RequestOnSink()
        {
            // it may block or not
            requestResult = tester.Execute(sinkThread, () => { var r = handle.RequestTimeInterval(out var i); return Tuple.Create(r, i); }, "Request");
            return requestResult;
        }

        private ThreadSyncTester.ExecutionResult ReportProgressOnSink(TimeInterval p)
        {
            return tester.Execute(sinkThread, () => { handle.ReportProgress(p); return null; }, "ReportProgress").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult DisposeOnExternal()
        {
            // dispose should never block
            return tester.Execute(externalThread, () => { handle.Dispose(); return null; }, "Dispose").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult SetSourceSideActiveOnExternal(bool value)
        {
            // this should never block
            return tester.Execute(externalThread, () => { handle.SourceSideActive = value; return null; }, "SourceSideActive").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult SetEnabledOnExternal(bool value)
        {
            // this one can block!
            return tester.Execute(externalThread, (Func<object>)(() => { handle.Enabled = value; return null; }), "Enabled");
        }

        private ThreadSyncTester.ExecutionResult BreakOnSink(TimeInterval? i = null)
        {
            // this should never block
            return tester.Execute(sinkThread, () => { handle.ReportBackAndBreak(i ?? TimeInterval.Empty); return null; }, "Break").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult ContinueOnSink(TimeInterval? i = null)
        {
            // this should never block
            return tester.Execute(sinkThread, () => { handle.ReportBackAndContinue(i ?? TimeInterval.Empty); return null; }, "Continue").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult LatchOnSource()
        {
            // this should never block
            return tester.Execute(sourceThread, () => { handle.Latch(); return null; }, "Latch").ShouldFinish();
        }

        private ThreadSyncTester.ExecutionResult UnlatchOnSource()
        {
            // this should never block
            return tester.Execute(sourceThread, () => { handle.Unlatch(); return null; }, "Unlatch").ShouldFinish();
        }

        private void ShouldWaitReturn(bool result, bool isActivatedRecently, TimeInterval interval)
        {
            Assert.AreEqual(Tuple.Create(result, isActivatedRecently, interval), waitResult.Result, "wait result");
        }

        private void ShouldRequestReturn(bool result, TimeInterval interval)
        {
            Assert.AreEqual(Tuple.Create(result, interval), requestResult.Result, "request result");
        }

        private void Finish()
        {
            tester.Finish();
        }

        private MockTimeSource tsource;
        private ThreadSyncTester tester;
        private TimeHandle handle;
        private ThreadSyncTester.TestThread sourceThread;
        private ThreadSyncTester.TestThread sinkThread;
        private ThreadSyncTester.TestThread externalThread;
        private TimeInterval interval;
        private ThreadSyncTester.ExecutionResult waitResult;
        private ThreadSyncTester.ExecutionResult requestResult;

        private class MockTimeSource : ITimeSource, ITimeDomain
        {
            public void RegisterSink(ITimeSink sink)
            {
                throw new NotImplementedException();
            }

            public void ReportHandleActive()
            {
            }

            public void ReportTimeProgress(ulong previousElapsedTicks)
            {
            }

            public TimeInterval Quantum { get; set; }

            public ITimeDomain Domain => this;

            public TimeInterval NearestSyncPoint => ElapsedVirtualTime + Quantum;

            public TimeInterval ElapsedVirtualTime { get; set; }

            public event Action StopRequested
            {
                add {}
                remove {}
            }
        }

        private class MockTimeSink : ITimeSink
        {
            public TimeHandle TimeHandle { get; set; }
        }
    }
}
