// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*=============================================================================
**
**
**
** Purpose: Synchronizes access to a shared resource or region of code in a multi-threaded
**             program.
**
**
=============================================================================*/

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Internal.Runtime.CompilerServices;

namespace System.Threading
{
    public static partial class Monitor
    {
        #region Object->Lock/Condition mapping

        private static readonly ConditionalWeakTable<object, Condition> s_conditionTable = new ConditionalWeakTable<object, Condition>();
        private static readonly ConditionalWeakTable<object, Condition>.CreateValueCallback s_createCondition = (o) => new Condition(ObjectHeader.GetLockObject(o));

        private static Condition GetCondition(object obj)
        {
            Debug.Assert(
                !(obj is Condition || obj is Lock),
                "Do not use Monitor.Pulse or Wait on a Lock or Condition instance; use the methods on Condition instead.");
            return s_conditionTable.GetValue(obj, s_createCondition);
        }
        #endregion

        #region Public Enter/Exit methods

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Enter(object obj)
        {
            int currentThreadID = ManagedThreadId.CurrentManagedThreadIdUnchecked;
            int resultOrIndex = ObjectHeader.Acquire(obj, currentThreadID);
            if (resultOrIndex < 0)
                return;

            Lock lck = resultOrIndex == 0 ?
                ObjectHeader.GetLockObject(obj) :
                SyncTable.GetLockObject(resultOrIndex);

            lck.TryEnterSlow(Timeout.Infinite, currentThreadID, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Enter(object obj, ref bool lockTaken)
        {
            // we are inlining lockTaken check as the check is likely be optimized away
            if (lockTaken)
                throw new ArgumentException(SR.Argument_MustBeFalse, nameof(lockTaken));

            Enter(obj);
            lockTaken = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryEnter(object obj)
        {
            int currentThreadID = ManagedThreadId.CurrentManagedThreadIdUnchecked;
            int resultOrIndex = ObjectHeader.TryAcquire(obj, currentThreadID);
            if (resultOrIndex < 0)
                return true;

            if (resultOrIndex == 0)
                return false;

            Lock lck = SyncTable.GetLockObject(resultOrIndex);

            // The one-shot fast path is not covered by the slow path below for a zero timeout when the thread ID is
            // initialized, so cover it here in case it wasn't already done
            if (currentThreadID != 0 && lck.TryEnterOneShot(currentThreadID))
                return true;

            return lck.TryEnterSlow(0, currentThreadID, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TryEnter(object obj, ref bool lockTaken)
        {
            // we are inlining lockTaken check as the check is likely be optimized away
            if (lockTaken)
                throw new ArgumentException(SR.Argument_MustBeFalse, nameof(lockTaken));

            lockTaken = TryEnter(obj);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryEnter(object obj, int millisecondsTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);

            int currentThreadID = ManagedThreadId.CurrentManagedThreadIdUnchecked;
            int resultOrIndex = ObjectHeader.TryAcquire(obj, currentThreadID);
            if (resultOrIndex < 0)
                return true;

            Lock lck = resultOrIndex == 0 ?
                ObjectHeader.GetLockObject(obj) :
                SyncTable.GetLockObject(resultOrIndex);

            // The one-shot fast path is not covered by the slow path below for a zero timeout when the thread ID is
            // initialized, so cover it here in case it wasn't already done
            if (millisecondsTimeout == 0 && currentThreadID != 0 && lck.TryEnterOneShot(currentThreadID))
                return true;

            return lck.TryEnterSlow(millisecondsTimeout, currentThreadID, obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TryEnter(object obj, int millisecondsTimeout, ref bool lockTaken)
        {
            // we are inlining lockTaken check as the check is likely be optimized away
            if (lockTaken)
                throw new ArgumentException(SR.Argument_MustBeFalse, nameof(lockTaken));

            lockTaken = TryEnter(obj, millisecondsTimeout);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Exit(object obj)
        {
            ObjectHeader.Release(obj);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsEntered(object obj)
        {
            return ObjectHeader.IsAcquired(obj);
        }

        #endregion

        #region Public Wait/Pulse methods

        [UnsupportedOSPlatform("browser")]
        public static bool Wait(object obj, int millisecondsTimeout)
        {
            Condition condition = GetCondition(obj);

            using (new DebugBlockingScope(obj, DebugBlockingItemType.MonitorEvent, millisecondsTimeout, out _))
            {
                return condition.Wait(millisecondsTimeout);
            }
        }

        public static void Pulse(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            GetCondition(obj).SignalOne();
        }

        public static void PulseAll(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            GetCondition(obj).SignalAll();
        }

        #endregion

        #region Debugger support

        // The debugger binds to the fields below by name. Do not change any names or types without
        // updating the debugger!

        // The head of the list of DebugBlockingItem stack objects used by the debugger to implement
        // ICorDebugThread4::GetBlockingObjects. Usually the list either is empty or contains a single
        // item. However, a wait on an STA thread may reenter via the message pump and cause the thread
        // to be blocked on a second object.
        [ThreadStatic]
        private static IntPtr t_firstBlockingItem;

        // Different ways a thread can be blocked that the debugger will expose.
        // Do not change or add members without updating the debugger code.
        internal enum DebugBlockingItemType
        {
            MonitorCriticalSection = 0,
            MonitorEvent = 1
        }

        // Represents an item a thread is blocked on. This structure is allocated on the stack and accessed by the debugger.
        internal struct DebugBlockingItem
        {
            // The object the thread is waiting on
            public object _object;

            // Indicates how the thread is blocked on the item
            public DebugBlockingItemType _blockingType;

            // Blocking timeout in milliseconds or Timeout.Infinite for no timeout
            public int _timeout;

            // Next pointer in the linked list of DebugBlockingItem records
            public IntPtr _next;
        }

        internal unsafe struct DebugBlockingScope : IDisposable
        {
            public DebugBlockingScope(object obj, DebugBlockingItemType blockingType, int timeout, out DebugBlockingItem blockingItem)
            {
                blockingItem._object = obj;
                blockingItem._blockingType = blockingType;
                blockingItem._timeout = timeout;
                blockingItem._next = t_firstBlockingItem;

                t_firstBlockingItem = (IntPtr)Unsafe.AsPointer(ref blockingItem);
            }

            public void Dispose()
            {
                t_firstBlockingItem = Unsafe.Read<DebugBlockingItem>((void*)t_firstBlockingItem)._next;
            }
        }

        #endregion

        #region Metrics

        /// <summary>
        /// Gets the number of times there was contention upon trying to take a <see cref="Monitor"/>'s lock so far.
        /// </summary>
        public static long LockContentionCount => Lock.ContentionCount;

        #endregion
    }
}
