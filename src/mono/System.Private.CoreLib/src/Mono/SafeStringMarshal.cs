// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Safe wrapper for a string and its UTF8 encoding
//
// Authors:
//   Aleksey Kliger <aleksey@xamarin.com>
//   Rodrigo Kumpera <kumpera@xamarin.com>
//
//

using System;
using System.Runtime.InteropServices;

namespace Mono
{
    internal struct SafeStringMarshal : IDisposable
    {
        private IntPtr marshaled_string;

        public SafeStringMarshal(string? str)
        {
            marshaled_string = Marshal.StringToCoTaskMemUTF8(str);
        }

        public IntPtr Value => marshaled_string;

        public void Dispose()
        {
            if (marshaled_string != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(marshaled_string);
                marshaled_string = IntPtr.Zero;
            }
        }
    }
}
