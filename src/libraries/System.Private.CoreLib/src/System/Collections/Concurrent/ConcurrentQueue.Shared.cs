// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace System.Collections.Concurrent
{
    [DebuggerDisplay("Count = {Count}")]
    [DebuggerTypeProxy(typeof(IProducerConsumerCollectionDebugView<>))]
    public partial class ConcurrentQueue<T> : IProducerConsumerCollection<T>, IReadOnlyCollection<T>
    {
        void ICollection.CopyTo(Array array, int index)
        {
            if (array is T[] szArray)
            {
                CopyTo(szArray, index);
                return;
            }

            if (array is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
            }

            ToArray().CopyTo(array, index);
        }

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot { get { ThrowHelper.ThrowNotSupportedException(ExceptionResource.ConcurrentCollection_SyncRoot_NotSupported); return default; } }

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

        bool IProducerConsumerCollection<T>.TryAdd(T item)
        {
            Enqueue(item);
            return true;
        }

        bool IProducerConsumerCollection<T>.TryTake([MaybeNullWhen(false)] out T item) => TryDequeue(out item);
    }
}
