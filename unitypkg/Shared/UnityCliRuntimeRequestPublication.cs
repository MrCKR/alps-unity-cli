using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityCli
{
    /// <summary>Runtime request cache的纯事务reservation；只在enqueue成功后提交淘汰。</summary>
    public sealed class UnityCliRuntimeRequestPublication<T>
    {
        public sealed class Reservation
        {
            internal Reservation(string requestId, string requestDigest, string evictionRequestId, T evictionCandidate)
            {
                RequestId = requestId;
                RequestDigest = requestDigest;
                EvictionRequestId = evictionRequestId;
                EvictionCandidate = evictionCandidate;
            }
            public string RequestId { get; private set; }
            public string RequestDigest { get; private set; }
            public string EvictionRequestId { get; private set; }
            internal T EvictionCandidate { get; private set; }
            public bool HasEviction { get { return EvictionRequestId != null; } }
        }

        private readonly int capacity;
        private readonly Dictionary<string, T> entries = new Dictionary<string, T>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> digests = new Dictionary<string, string>(StringComparer.Ordinal);

        public UnityCliRuntimeRequestPublication(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
        }

        public int Count { get { return entries.Count; } }
        public IEnumerable<T> Values { get { return entries.Values.ToArray(); } }

        public bool TryGet(string requestId, out T value)
        {
            return entries.TryGetValue(requestId, out value);
        }

        public bool IsIdentityMatch(string requestId, string requestDigest)
        {
            string digest;
            return digests.TryGetValue(requestId, out digest) && string.Equals(digest, requestDigest, StringComparison.Ordinal);
        }

        /// <summary>规划只读且可重复；无可淘汰terminal时不修改当前cache。</summary>
        public Reservation Plan(string requestId, string requestDigest, Func<T, bool> isCompleted, Func<T, long> order)
        {
            if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("requestId is required.", "requestId");
            if (string.IsNullOrWhiteSpace(requestDigest)) throw new ArgumentException("requestDigest is required.", "requestDigest");
            if (isCompleted == null) throw new ArgumentNullException("isCompleted");
            if (order == null) throw new ArgumentNullException("order");
            T existing;
            if (entries.TryGetValue(requestId, out existing))
            {
                if (!IsIdentityMatch(requestId, requestDigest)) throw new InvalidOperationException("Request identity does not match the existing cache entry.");
                return new Reservation(requestId, requestDigest, null, existing);
            }
            if (entries.Count < capacity) return new Reservation(requestId, requestDigest, null, default(T));
            var completed = entries.Where(pair => isCompleted(pair.Value)).OrderBy(pair => order(pair.Value)).ToArray();
            return completed.Length == 0 ? null : new Reservation(requestId, requestDigest, completed[0].Key, completed[0].Value);
        }

        /// <summary>先加入新entry并提交enqueue；enqueue失败时旧entry保持不变，新entry回滚。</summary>
        public bool Publish(Reservation reservation, T value, Func<T, bool> enqueue)
        {
            if (reservation == null) throw new ArgumentNullException("reservation");
            if (enqueue == null) throw new ArgumentNullException("enqueue");
            if (entries.ContainsKey(reservation.RequestId)) return IsIdentityMatch(reservation.RequestId, reservation.RequestDigest);
            if (entries.Count >= capacity && !reservation.HasEviction) return false;
            if (reservation.HasEviction)
            {
                T current;
                if (!entries.TryGetValue(reservation.EvictionRequestId, out current) || !object.ReferenceEquals(current, reservation.EvictionCandidate)) return false;
            }
            entries.Add(reservation.RequestId, value);
            digests.Add(reservation.RequestId, reservation.RequestDigest);
            try
            {
                if (!enqueue(value))
                {
                    entries.Remove(reservation.RequestId);
                    digests.Remove(reservation.RequestId);
                    return false;
                }
                if (reservation.HasEviction)
                {
                    entries.Remove(reservation.EvictionRequestId);
                    digests.Remove(reservation.EvictionRequestId);
                }
                return true;
            }
            catch
            {
                entries.Remove(reservation.RequestId);
                digests.Remove(reservation.RequestId);
                throw;
            }
        }

        public void Clear() { entries.Clear(); digests.Clear(); }
    }
}
