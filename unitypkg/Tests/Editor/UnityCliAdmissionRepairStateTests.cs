using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliAdmissionRepairStateTests
    {
        private sealed class Entry
        {
            public Entry(long order, bool completed) { Order = order; Completed = completed; }
            public long Order;
            public bool Completed;
        }

        [Test]
        public void RuntimeReservationDoesNotEvictOnWrongIdentityOrQueueFailure()
        {
            var store = new UnityCliRuntimeRequestPublication<Entry>(2);
            Publish(store, "old", 1, true);
            Publish(store, "active", 2, false);
            Assert.Throws<InvalidOperationException>(() => store.Plan("old", "wrong", entry => entry.Completed, entry => entry.Order));
            Assert.That(store.Count, Is.EqualTo(2));
            var fullActive = new UnityCliRuntimeRequestPublication<Entry>(2);
            Publish(fullActive, "active-a", 1, false);
            Publish(fullActive, "active-b", 2, false);
            Assert.That(fullActive.Plan("active-c", "3", entry => entry.Completed, entry => entry.Order), Is.Null);

            var reservation = store.Plan("new", "3", entry => entry.Completed, entry => entry.Order);
            Assert.That(reservation.HasEviction, Is.True);
            Assert.That(store.Publish(reservation, new Entry(3, false), entry => false), Is.False);
            Entry retained;
            Assert.That(store.TryGet("old", out retained), Is.True);
            Assert.That(store.Count, Is.EqualTo(2));
            store.Clear();
            Assert.That(store.Count, Is.EqualTo(0));
        }

        [Test]
        public void RuntimeReservationCommitsExactlyOneTerminalEvictionAfterEnqueue()
        {
            var store = new UnityCliRuntimeRequestPublication<Entry>(2);
            Publish(store, "first", 1, true);
            Publish(store, "second", 2, true);
            var reservation = store.Plan("third", "3", entry => entry.Completed, entry => entry.Order);
            var enqueueCount = 0;
            Assert.That(store.Publish(reservation, new Entry(3, false), entry => { enqueueCount++; return true; }), Is.True);
            Assert.That(enqueueCount, Is.EqualTo(1));
            Entry ignored;
            Assert.That(store.TryGet("first", out ignored), Is.False);
            Assert.That(store.TryGet("second", out ignored), Is.True);
            Assert.That(store.TryGet("third", out ignored), Is.True);
            Assert.That(store.Count, Is.EqualTo(2));
        }

        [Test]
        public void QueuePublicationRollsBackMapAndQueueAfterBarrierFailure()
        {
            var map = new HashSet<string>(StringComparer.Ordinal);
            var queue = new List<string>();
            Assert.Throws<InvalidOperationException>(() => UnityCliQueuePublication.Publish(
                () => map.Add("request"),
                () => queue.Add("request"),
                () => map.Remove("request"),
                () => queue.Remove("request"),
                () => { throw new InvalidOperationException("publication barrier"); }));
            Assert.That(map, Is.Empty);
            Assert.That(queue, Is.Empty);
        }

        [Test]
        public void TryStartDeadlineListenerAndAcceptanceRacesHaveOneOutcome()
        {
            var deadline = new UnityCliBridgeWorkItemState();
            Assert.That(deadline.TryStart(true, 0), Is.EqualTo(UnityCliBridgeWorkItemState.StartOutcome.DeadlineElapsed));
            Assert.That(deadline.TryAccept(), Is.False);

            var reload = new UnityCliBridgeWorkItemState();
            Assert.That(reload.TryStart(false, 100), Is.EqualTo(UnityCliBridgeWorkItemState.StartOutcome.ListenerChanged));
            Assert.That(reload.TryAccept(), Is.False);

            var accepted = new UnityCliBridgeWorkItemState();
            Assert.That(accepted.TryAccept(), Is.True);
            Assert.That(accepted.CancelBeforeAcceptance(), Is.False);
            Assert.That(accepted.TryStart(true, 100), Is.EqualTo(UnityCliBridgeWorkItemState.StartOutcome.Started));
        }

        private static void Publish(UnityCliRuntimeRequestPublication<Entry> store, string id, long order, bool completed)
        {
            var reservation = store.Plan(id, order.ToString(), entry => entry.Completed, entry => entry.Order);
            Assert.That(store.Publish(reservation, new Entry(order, completed), entry => true), Is.True);
        }
    }
}
