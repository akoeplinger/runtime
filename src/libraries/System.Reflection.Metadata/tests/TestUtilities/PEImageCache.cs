// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace System.Reflection.Metadata.Tests
{
    internal static class PEImageCache
    {
        private static readonly Dictionary<byte[], GCHandle> s_peImages = new Dictionary<byte[], GCHandle>();

        internal static unsafe MetadataReader GetMetadataReader(byte[] peImage, MetadataReaderOptions options = MetadataReaderOptions.Default, MetadataStringDecoder decoder = null)
            => GetMetadataReader(peImage, out var _, options, decoder);

        internal static unsafe MetadataReader GetMetadataReader(byte[] peImage, out int metadataStartOffset, MetadataReaderOptions options = MetadataReaderOptions.Default, MetadataStringDecoder decoder = null)
        {
            GCHandle pinned = GetPinnedPEImage(peImage);
            var headers = new PEHeaders(new MemoryStream(peImage));
            metadataStartOffset = headers.MetadataStartOffset;
            return new MetadataReader((byte*)pinned.AddrOfPinnedObject() + headers.MetadataStartOffset, headers.MetadataSize, options, decoder);
        }

        internal static unsafe PEReader GetPEReader(byte[] peImage)
        {
            var pinned = GetPinnedPEImage(peImage);
            return new PEReader((byte*)pinned.AddrOfPinnedObject(), peImage.Length);
        }

        internal static unsafe GCHandle GetPinnedPEImage(byte[] peImage)
        {
            lock (s_peImages)
            {
                GCHandle pinned;
                if (!s_peImages.TryGetValue(peImage, out pinned))
                {
                    s_peImages.Add(peImage, pinned = GCHandle.Alloc(peImage, GCHandleType.Pinned));
                }

                return pinned;
            }
        }
    }
}
