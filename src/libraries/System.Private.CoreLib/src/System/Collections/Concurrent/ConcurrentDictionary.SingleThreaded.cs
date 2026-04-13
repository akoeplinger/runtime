// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Collections.Concurrent
{
    /// <summary>Represents a thread-safe collection of keys and values.</summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    /// <remarks>
    /// All public and protected members of <see cref="ConcurrentDictionary{TKey,TValue}"/> are thread-safe and may be used
    /// concurrently from multiple threads.
    /// </remarks>
    public partial class ConcurrentDictionary<TKey, TValue>
    {
        // This implementation is used on single-threaded platforms like browser-wasm.
        // Key design differences from the multi-threaded version:
        //
        // - Backed by a plain Dictionary<TKey, TValue> instead of a custom striped-lock hash table.
        //   All locking-related methods (AcquireAllLocks, ReleaseLocks) are no-ops that are compiled away.
        //
        // - GetEnumerator uses a copy-on-write approach: in the common case (no modification during
        //   enumeration), it walks the dictionary directly with zero extra allocation. If the dictionary
        //   is mutated mid-enumeration, the mutation clones the dictionary first so the enumerator
        //   continues walking the old (now-frozen) copy without throwing or producing duplicates.
        //
        private Dictionary<TKey, TValue> _dictionary;
        private readonly int _initialCapacity;
        private int _activeEnumeratorsOnCurrentCopy;

        internal ConcurrentDictionary(int concurrencyLevel, int capacity, bool growLockArray, IEqualityComparer<TKey>? comparer)
        {
            _ = growLockArray;

            if (concurrencyLevel <= 0 && concurrencyLevel != -1)
            {
                throw new ArgumentOutOfRangeException(nameof(concurrencyLevel), SR.ConcurrentDictionary_ConcurrencyLevelMustBePositiveOrNegativeOne);
            }

            _initialCapacity = capacity;
            _dictionary = new Dictionary<TKey, TValue>(capacity, comparer);
        }

        [Conditional("FEATURE_MULTITHREADING")]
        private void AcquireAllLocks(ref int locksAcquired) { Debug.Assert(_dictionary is not null); }

        [Conditional("FEATURE_MULTITHREADING")]
        private void ReleaseLocks(int locksAcquired) { Debug.Assert(_dictionary is not null); }

        private int GetCountNoLocks() => _dictionary.Count;

        /// <summary>
        /// If an enumerator is active, clones the dictionary so the enumerator can continue
        /// iterating the old copy while mutations proceed on the new one.
        /// When <paramref name="reset"/> is true, replaces the dictionary with an empty one
        /// at the initial capacity (used by Clear).
        /// </summary>
        private void CopyOnWrite(bool reset = false)
        {
            if (reset || _activeEnumeratorsOnCurrentCopy > 0)
            {
                _dictionary = reset
                    ? new Dictionary<TKey, TValue>(_initialCapacity, _dictionary.Comparer)
                    : new Dictionary<TKey, TValue>(_dictionary, _dictionary.Comparer);
                _activeEnumeratorsOnCurrentCopy = 0;
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToPairs(KeyValuePair<TKey, TValue>[] array, int index)
        {
            ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).CopyTo(array, index);
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToEntries(DictionaryEntry[] array, int index)
        {
            foreach (KeyValuePair<TKey, TValue> kvp in _dictionary)
            {
                array[index++] = new DictionaryEntry(kvp.Key, kvp.Value);
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToObjects(object[] array, int index)
        {
            foreach (KeyValuePair<TKey, TValue> kvp in _dictionary)
            {
                array[index++] = kvp;
            }
        }

        private void InitializeFromCollection(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            _dictionary = new Dictionary<TKey, TValue>(collection, _dictionary.Comparer);
        }

        /// <summary>
        /// Gets a collection containing the keys in the dictionary.
        /// </summary>
        private ReadOnlyCollection<TKey> GetKeys()
        {
            int count = _dictionary.Count;
            if (count == 0)
            {
                return ReadOnlyCollection<TKey>.Empty;
            }

            var keys = new TKey[count];
            _dictionary.Keys.CopyTo(keys, 0);

            return new ReadOnlyCollection<TKey>(keys);
        }

        /// <summary>
        /// Gets a collection containing the values in the dictionary.
        /// </summary>
        private ReadOnlyCollection<TValue> GetValues()
        {
            int count = _dictionary.Count;
            if (count == 0)
            {
                return ReadOnlyCollection<TValue>.Empty;
            }

            var values = new TValue[count];
            _dictionary.Values.CopyTo(values, 0);

            return new ReadOnlyCollection<TValue>(values);
        }

        private bool TryAddInternal(TKey key, TValue value, bool updateIfExists, out TValue resultingValue)
        {
            CopyOnWrite();

            if (_dictionary.TryAdd(key, value))
            {
                resultingValue = value;
                return true;
            }

            if (updateIfExists)
            {
                _dictionary[key] = value;
            }

            resultingValue = updateIfExists ? value : _dictionary[key];
            return false;
        }

        /// <summary>
        /// Attempts to get the value associated with the specified key from the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the value to get.</param>
        /// <param name="value">
        /// When this method returns, <paramref name="value"/> contains the object from
        /// the <see cref="ConcurrentDictionary{TKey,TValue}"/> with the specified key or the default value of
        /// <typeparamref name="TValue"/>, if the operation failed.
        /// </param>
        /// <returns>true if the key was found in the <see cref="ConcurrentDictionary{TKey,TValue}"/>; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _dictionary.TryGetValue(key, out value);

        /// <summary>
        /// Removes the specified key from the dictionary if it exists and returns its associated value.
        /// If matchValue flag is set, the key will be removed only if is associated with a particular
        /// value.
        /// </summary>
        /// <param name="key">The key to search for and remove if it exists.</param>
        /// <param name="value">The variable into which the removed value, if found, is stored.</param>
        /// <param name="matchValue">Whether removal of the key is conditional on its value.</param>
        /// <param name="oldValue">The conditional value to compare against if <paramref name="matchValue"/> is true</param>
        private bool TryRemoveInternal(TKey key, [MaybeNullWhen(false)] out TValue value, bool matchValue, TValue? oldValue)
        {
            if (_dictionary.TryGetValue(key, out value))
            {
                if (!matchValue || EqualityComparer<TValue>.Default.Equals(value, oldValue))
                {
                    CopyOnWrite();
                    _dictionary.Remove(key);
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Updates the value associated with <paramref name="key"/> to <paramref name="newValue"/> if the existing value is equal
        /// to <paramref name="comparisonValue"/>.
        /// </summary>
        /// <param name="key">The key whose value is compared with <paramref name="comparisonValue"/> and
        /// possibly replaced.</param>
        /// <param name="newValue">The value that replaces the value of the element with <paramref
        /// name="key"/> if the comparison results in equality.</param>
        /// <param name="comparisonValue">The value that is compared to the value of the element with
        /// <paramref name="key"/>.</param>
        /// <returns>
        /// true if the value with <paramref name="key"/> was equal to <paramref name="comparisonValue"/> and
        /// replaced with <paramref name="newValue"/>; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            if (_dictionary.TryGetValue(key, out TValue? existing) &&
                EqualityComparer<TValue>.Default.Equals(existing, comparisonValue))
            {
                CopyOnWrite();
                _dictionary[key] = newValue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes all keys and values from the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        public void Clear() => CopyOnWrite(reset: true);

        /// <summary>Returns an enumerator that iterates through the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>.</summary>
        /// <returns>An enumerator for the <see cref="ConcurrentDictionary{TKey,TValue}"/>.</returns>
        /// <remarks>
        /// The enumerator returned from the dictionary is safe to use concurrently with
        /// reads and writes to the dictionary, however it does not represent a moment-in-time snapshot
        /// of the dictionary.  The contents exposed through the enumerator may contain modifications
        /// made to the dictionary after <see cref="GetEnumerator"/> was called.
        /// </remarks>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            _activeEnumeratorsOnCurrentCopy++;
            return new Enumerator(this);
        }

        /// <summary>Enumerator that walks the dictionary directly. If the dictionary is mutated
        /// during enumeration, the mutating operation clones the dictionary first (copy-on-write),
        /// so this enumerator continues walking the old, now-frozen copy.</summary>
        private sealed class Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly ConcurrentDictionary<TKey, TValue> _parent;
            private readonly Dictionary<TKey, TValue> _dictionary;
            private Dictionary<TKey, TValue>.Enumerator _enumerator;
            private bool _disposed;

            internal Enumerator(ConcurrentDictionary<TKey, TValue> parent)
            {
                _parent = parent;
                _dictionary = parent._dictionary;
                _enumerator = _dictionary.GetEnumerator();
            }

            public KeyValuePair<TKey, TValue> Current => _enumerator.Current;

            object IEnumerator.Current => Current;

            public bool MoveNext() => _enumerator.MoveNext();

            public void Reset() => _enumerator = _dictionary.GetEnumerator();

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _enumerator.Dispose();
                    _parent._activeEnumeratorsOnCurrentCopy--;
                }
            }
        }

        /// <summary>Gets or sets the value associated with the specified key.</summary>
        /// <param name="key">The key of the value to get or set.</param>
        /// <value>
        /// The value associated with the specified key. If the specified key is not found, a get operation throws a
        /// <see cref="KeyNotFoundException"/>, and a set operation creates a new element with the specified key.
        /// </value>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="key"/> is a null reference (Nothing in Visual Basic).
        /// </exception>
        /// <exception cref="KeyNotFoundException">
        /// The property is retrieved and <paramref name="key"/> does not exist in the collection.
        /// </exception>
        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set
            {
                CopyOnWrite();
                _dictionary[key] = value;
            }
        }

        /// <summary>
        /// Gets the <see cref="IEqualityComparer{TKey}" />
        /// that is used to determine equality of keys for the dictionary.
        /// </summary>
        /// <value>
        /// The <see cref="IEqualityComparer{TKey}" /> generic interface implementation
        /// that is used to determine equality of keys for the current
        /// <see cref="ConcurrentDictionary{TKey, TValue}" /> and to provide hash values for the keys.
        /// </value>
        /// <remarks>
        /// <see cref="ConcurrentDictionary{TKey, TValue}" /> requires an equality implementation to determine
        /// whether keys are equal. You can specify an implementation of the <see cref="IEqualityComparer{TKey}" />
        /// generic interface by using a constructor that accepts a comparer parameter;
        /// if you do not specify one, the default generic equality comparer <see cref="EqualityComparer{TKey}.Default" /> is used.
        /// </remarks>
        public IEqualityComparer<TKey> Comparer => _dictionary.Comparer;

        /// <summary>
        /// Gets an instance of a type that may be used to perform operations on a <see cref="ConcurrentDictionary{TKey, TValue}"/>
        /// using a <typeparamref name="TAlternateKey"/> as a key instead of a <typeparamref name="TKey"/>.
        /// </summary>
        /// <typeparam name="TAlternateKey">The alternate type of a key for performing lookups.</typeparam>
        /// <returns>The created lookup instance.</returns>
        /// <exception cref="InvalidOperationException">This instance's comparer is not compatible with <typeparamref name="TAlternateKey"/>.</exception>
        /// <remarks>
        /// This instance must be using a comparer that implements <see cref="IAlternateEqualityComparer{TAlternateKey, TKey}"/> with
        /// <typeparamref name="TAlternateKey"/> and <typeparamref name="TKey"/>. If it doesn't, an exception will be thrown.
        /// </remarks>
        public AlternateLookup<TAlternateKey> GetAlternateLookup<TAlternateKey>() where TAlternateKey : notnull, allows ref struct
        {
            if (_dictionary.Comparer is not IAlternateEqualityComparer<TAlternateKey, TKey>)
            {
                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_IncompatibleComparer);
            }

            return new AlternateLookup<TAlternateKey>(this);
        }

        /// <summary>
        /// Gets an instance of a type that may be used to perform operations on a <see cref="ConcurrentDictionary{TKey, TValue}"/>
        /// using a <typeparamref name="TAlternateKey"/> as a key instead of a <typeparamref name="TKey"/>.
        /// </summary>
        /// <typeparam name="TAlternateKey">The alternate type of a key for performing lookups.</typeparam>
        /// <param name="lookup">The created lookup instance when the method returns true, or a default instance that should not be used if the method returns false.</param>
        /// <returns>true if a lookup could be created; otherwise, false.</returns>
        /// <remarks>
        /// This instance must be using a comparer that implements <see cref="IAlternateEqualityComparer{TAlternateKey, TKey}"/> with
        /// <typeparamref name="TAlternateKey"/> and <typeparamref name="TKey"/>. If it doesn't, the method will return false.
        /// </remarks>
        public bool TryGetAlternateLookup<TAlternateKey>(out AlternateLookup<TAlternateKey> lookup) where TAlternateKey : notnull, allows ref struct
        {
            if (_dictionary.Comparer is IAlternateEqualityComparer<TAlternateKey, TKey>)
            {
                lookup = new AlternateLookup<TAlternateKey>(this);
                return true;
            }

            lookup = default;
            return false;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="valueFactory">The function used to generate a value for the key</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value for the key as returned by valueFactory
        /// if the key was not in the dictionary.</returns>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (valueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.valueFactory);
            }

            if (_dictionary.TryGetValue(key, out TValue? value))
            {
                return value;
            }

            value = valueFactory(key);
            CopyOnWrite();
            _dictionary[key] = value;

            return value;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="valueFactory">The function used to generate a value for the key</param>
        /// <param name="factoryArgument">An argument value to pass into <paramref name="valueFactory"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value for the key as returned by valueFactory
        /// if the key was not in the dictionary.</returns>
        public TValue GetOrAdd<TArg>(TKey key, Func<TKey, TArg, TValue> valueFactory, TArg factoryArgument)
            where TArg : allows ref struct
        {
            if (valueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.valueFactory);
            }

            if (_dictionary.TryGetValue(key, out TValue? value))
            {
                return value;
            }

            value = valueFactory(key, factoryArgument);
            CopyOnWrite();
            _dictionary[key] = value;

            return value;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">the value to be added, if the key does not already exist</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value if the key was not in the dictionary.</returns>
        public TValue GetOrAdd(TKey key, TValue value)
        {
            if (_dictionary.TryGetValue(key, out TValue? existing))
            {
                return existing;
            }

            CopyOnWrite();
            _dictionary[key] = value;

            return value;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key does not already
        /// exist, or updates a key/value pair in the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValueFactory">The function used to generate a value for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key
        /// based on the key's existing value</param>
        /// <param name="factoryArgument">An argument to pass into <paramref name="addValueFactory"/> and <paramref name="updateValueFactory"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="addValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="updateValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The new value for the key.  This will be either be the result of addValueFactory (if the key was
        /// absent) or the result of updateValueFactory (if the key was present).</returns>
        public TValue AddOrUpdate<TArg>(
            TKey key, Func<TKey, TArg, TValue> addValueFactory, Func<TKey, TValue, TArg, TValue> updateValueFactory, TArg factoryArgument)
            where TArg : allows ref struct
        {
            if (addValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.addValueFactory);
            }

            if (updateValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.updateValueFactory);
            }

            TValue newValue = _dictionary.TryGetValue(key, out TValue? oldValue)
                ? updateValueFactory(key, oldValue, factoryArgument)
                : addValueFactory(key, factoryArgument);

            CopyOnWrite();
            _dictionary[key] = newValue;

            return newValue;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key does not already
        /// exist, or updates a key/value pair in the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValueFactory">The function used to generate a value for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key
        /// based on the key's existing value</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="addValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="updateValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The new value for the key.  This will be either the result of addValueFactory (if the key was
        /// absent) or the result of updateValueFactory (if the key was present).</returns>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (addValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.addValueFactory);
            }

            if (updateValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.updateValueFactory);
            }

            TValue newValue = _dictionary.TryGetValue(key, out TValue? oldValue)
                ? updateValueFactory(key, oldValue)
                : addValueFactory(key);

            CopyOnWrite();
            _dictionary[key] = newValue;

            return newValue;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key does not already
        /// exist, or updates a key/value pair in the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValue">The value to be added for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key based on
        /// the key's existing value</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="updateValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The new value for the key.  This will be either the value of addValue (if the key was
        /// absent) or the result of updateValueFactory (if the key was present).</returns>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (updateValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.updateValueFactory);
            }

            TValue newValue = _dictionary.TryGetValue(key, out TValue? oldValue)
                ? updateValueFactory(key, oldValue)
                : addValue;

            CopyOnWrite();
            _dictionary[key] = newValue;

            return newValue;
        }

        /// <summary>
        /// Gets a value that indicates whether the <see cref="ConcurrentDictionary{TKey,TValue}"/> is empty.
        /// </summary>
        /// <value>true if the <see cref="ConcurrentDictionary{TKey,TValue}"/> is empty; otherwise,
        /// false.</value>
        public bool IsEmpty => _dictionary.Count == 0;

        public readonly partial struct AlternateLookup<TAlternateKey> where TAlternateKey : notnull, allows ref struct
        {
            internal AlternateLookup(ConcurrentDictionary<TKey, TValue> dictionary)
            {
                Debug.Assert(dictionary is not null);
                Dictionary = dictionary;
            }

            /// <summary>Attempts to add the specified key and value to the dictionary.</summary>
            /// <param name="key">The alternate key of the element to add.</param>
            /// <param name="value">The value of the element to add.</param>
            /// <param name="updateIfExists">true to overwrite the value of an existing key; false to throw for an existing key.</param>
            /// <param name="resultingValue">The value associated with the key when the operation completes.</param>
            private bool TryAdd(TAlternateKey key, TValue value, bool updateIfExists, out TValue resultingValue)
            {
                Dictionary.CopyOnWrite();
                var lookup = Dictionary._dictionary.GetAlternateLookup<TAlternateKey>();

                if (lookup.TryAdd(key, value))
                {
                    resultingValue = value;
                    return true;
                }

                if (updateIfExists)
                {
                    lookup[key] = value;
                }

                resultingValue = updateIfExists ? value : lookup[key];
                return false;
            }

            /// <summary>Gets the value associated with the specified alternate key.</summary>
            /// <param name="key">The alternate key of the value to get.</param>
            /// <param name="actualKey">
            /// When this method returns, contains the actual key associated with the alternate key, if the key is found;
            /// otherwise, the default value for the type of the key parameter.
            /// </param>
            /// <param name="value">
            /// When this method returns, contains the value associated with the specified key, if the key is found;
            /// otherwise, the default value for the type of the value parameter.
            /// </param>
            /// <returns><see langword="true"/> if an entry was found; otherwise, <see langword="false"/>.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryGetValue(TAlternateKey key, [MaybeNullWhen(false)] out TKey actualKey, [MaybeNullWhen(false)] out TValue value)
            {
                return Dictionary._dictionary.GetAlternateLookup<TAlternateKey>().TryGetValue(key, out actualKey, out value);
            }

            /// <summary>
            /// Removes the value with the specified alternate key from the <see cref="Dictionary{TKey, TValue}"/>,
            /// and copies the associated key and element to the value parameter.
            /// </summary>
            /// <param name="key">The alternate key of the element to remove.</param>
            /// <param name="actualKey">The removed key.</param>
            /// <param name="value">The removed element.</param>
            /// <returns>true if the element is successfully found and removed; otherwise, false.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryRemove(TAlternateKey key, [MaybeNullWhen(false)] out TKey actualKey, [MaybeNullWhen(false)] out TValue value)
            {
                Dictionary.CopyOnWrite();
                return Dictionary._dictionary.GetAlternateLookup<TAlternateKey>().Remove(key, out actualKey, out value);
            }
        }
    }
}
