﻿//
// Copyright 2020 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Apis.Util;
using Google.Solutions.Common.Diagnostics;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Google.Solutions.Ssh.Native
{
    /// <summary>
    /// An (unconnected) Libssh2 session.
    /// </summary>
    public class SshSession : IDisposable
    {
        private readonly SshSessionHandle sessionHandle;
        private bool disposed = false;

        internal static readonly UnsafeNativeMethods.Alloc AllocDelegate;
        internal static readonly UnsafeNativeMethods.Free FreeDelegate;
        internal static readonly UnsafeNativeMethods.Realloc ReallocDelegate;

        //---------------------------------------------------------------------
        // Ctor.
        //---------------------------------------------------------------------

        static SshSession()
        {
            // Store these delegates in fields to prevent them from being
            // garbage collected. Otherwise callbacks will suddenly
            // start hitting GC'ed memory.

            AllocDelegate = (size, context) => Marshal.AllocHGlobal(size);
            ReallocDelegate = (ptr, size, context) => Marshal.ReAllocHGlobal(ptr, size);
            FreeDelegate = (ptr, context) => Marshal.FreeHGlobal(ptr);

            try
            {
                var result = (LIBSSH2_ERROR)UnsafeNativeMethods.libssh2_init(0);
                if (result != LIBSSH2_ERROR.NONE)
                {
                    throw new SshNativeException(result);
                }
            }
            catch (EntryPointNotFoundException)
            {
                throw new SshException("libssh2 DLL not found or could not be loaded");
            }
        }

        public SshSession()
        {
            this.sessionHandle = UnsafeNativeMethods.libssh2_session_init_ex(
                AllocDelegate,
                FreeDelegate,
                ReallocDelegate,
                IntPtr.Zero);
        }

        public static string GetVersion(Version requiredVersion)
        {
            using (SshTraceSources.Default.TraceMethod().WithParameters(requiredVersion))
            {
                var requiredVersionEncoded =
                    (requiredVersion.Major << 16) |
                    (requiredVersion.Minor << 8) |
                    (requiredVersion.Build);

                return Marshal.PtrToStringAnsi(
                    UnsafeNativeMethods.libssh2_version(
                        requiredVersionEncoded));
            }
        }

        //---------------------------------------------------------------------
        // Algorithms.
        //---------------------------------------------------------------------

        public string[] GetSupportedAlgorithms(LIBSSH2_METHOD methodType)
        {
            using (SshTraceSources.Default.TraceMethod().WithParameters(methodType))
            {
                if (!Enum.IsDefined(typeof(LIBSSH2_METHOD), methodType))
                {
                    throw new ArgumentException(nameof(methodType));
                }

                lock (this.sessionHandle.SyncRoot)
                {
                    int count = UnsafeNativeMethods.libssh2_session_supported_algs(
                        this.sessionHandle,
                        methodType,
                        out IntPtr algorithmsPtrPtr);
                    if (count > 0 && algorithmsPtrPtr != IntPtr.Zero)
                    {
                        var algorithmsPtrs = new IntPtr[count];
                        Marshal.Copy(algorithmsPtrPtr, algorithmsPtrs, 0, algorithmsPtrs.Length);

                        var algorithms = algorithmsPtrs
                            .Select(ptr => Marshal.PtrToStringAnsi(ptr))
                            .ToArray();

                        UnsafeNativeMethods.libssh2_free(
                            this.sessionHandle,
                            algorithmsPtrPtr);

                        return algorithms;
                    }
                    else if (count < 0)
                    {
                        throw new SshNativeException((LIBSSH2_ERROR)count);
                    }
                    else
                    {
                        return Array.Empty<string>();
                    }
                }
            }
        }

        public void SetPreferredMethods(
            LIBSSH2_METHOD methodType,
            string[] methods)
        {
            using (SshTraceSources.Default.TraceMethod().WithParameters(
                methodType,
                methods))
            {
                if (!Enum.IsDefined(typeof(LIBSSH2_METHOD), methodType))
                {
                    throw new ArgumentException(nameof(methodType));
                }

                if (methods == null || methods.Length == 0)
                {
                    throw new ArgumentException(nameof(methods));
                }

                var prefs = string.Join(",", methods);

                lock (this.sessionHandle.SyncRoot)
                {
                    var result = (LIBSSH2_ERROR)UnsafeNativeMethods.libssh2_session_method_pref(
                        this.sessionHandle,
                        methodType,
                        prefs);
                    if (result != LIBSSH2_ERROR.NONE)
                    {
                        throw new SshNativeException(result);
                    }
                }
            }
        }

        //---------------------------------------------------------------------
        // Banner.
        //---------------------------------------------------------------------

        public void SetLocalBanner(string banner)
        {
            using (SshTraceSources.Default.TraceMethod().WithParameters(banner))
            {
                Utilities.ThrowIfNullOrEmpty(banner, nameof(banner));

                lock (this.sessionHandle.SyncRoot)
                {
                    UnsafeNativeMethods.libssh2_session_banner_set(
                        this.sessionHandle,
                        banner);
                }
            }
        }

        //---------------------------------------------------------------------
        // Timeout.
        //---------------------------------------------------------------------

        public TimeSpan Timeout
        {
            get
            {
                using (SshTraceSources.Default.TraceMethod().WithoutParameters())
                {
                    lock (this.sessionHandle.SyncRoot)
                    {
                        var millis = UnsafeNativeMethods.libssh2_session_get_timeout(
                            this.sessionHandle);
                        return TimeSpan.FromMilliseconds(millis);
                    }
                }
            }
            set
            {
                using (SshTraceSources.Default.TraceMethod().WithParameters(value))
                {
                    lock (this.sessionHandle.SyncRoot)
                    {
                        UnsafeNativeMethods.libssh2_session_set_timeout(
                            this.sessionHandle,
                            (int)value.TotalMilliseconds);
                    }
                }
            }
        }

        //---------------------------------------------------------------------
        // Handshake.
        //---------------------------------------------------------------------

        public Task<SshConnectedSession> ConnectAsync(EndPoint remoteEndpoint)
        {
            return Task.Run(() =>
            {
                using (SshTraceSources.Default.TraceMethod().WithParameters(remoteEndpoint))
                {
                    var socket = new Socket(
                        AddressFamily.InterNetwork,
                        SocketType.Stream,
                        ProtocolType.Tcp);

                    // TODO: Set SO_LINGER?

                    socket.Connect(remoteEndpoint);

                    lock (this.sessionHandle.SyncRoot)
                    {
                        var result = (LIBSSH2_ERROR)UnsafeNativeMethods.libssh2_session_handshake(
                            this.sessionHandle,
                            socket.Handle);
                        if (result != LIBSSH2_ERROR.NONE)
                        {
                            socket.Close();
                            throw new SshNativeException(result);
                        }

                        return new SshConnectedSession(this.sessionHandle, socket);
                    }
                }
            });
        }

        //---------------------------------------------------------------------
        // Error.
        //---------------------------------------------------------------------

        public LIBSSH2_ERROR LastError
        {
            get
            {
                lock (this.sessionHandle.SyncRoot)
                {
                    return (LIBSSH2_ERROR)UnsafeNativeMethods.libssh2_session_last_errno(
                        this.sessionHandle);
                }
            }
        }

        //---------------------------------------------------------------------
        // Tracing.
        //---------------------------------------------------------------------

        private UnsafeNativeMethods.TraceHandler TraceHandlerDelegate;

        public void SetTraceHandler(
            LIBSSH2_TRACE mask,
            Action<string> handler)
        {
            using (SshTraceSources.Default.TraceMethod().WithParameters(mask))
            {
                Utilities.ThrowIfNull(handler, nameof(handler));

                // Store this delegate in a field to prevent it from being
                // garbage collected. Otherwise callbacks will suddenly
                // start hitting GC'ed memory.
                this.TraceHandlerDelegate = (sessionPtr, contextPtr, dataPtr, length) =>
                {
                    Debug.Assert(contextPtr == IntPtr.Zero);

                    var data = new byte[length.ToInt32()];
                    Marshal.Copy(dataPtr, data, 0, length.ToInt32());

                    handler(Encoding.ASCII.GetString(data));
                };

                lock (this.sessionHandle.SyncRoot)
                {
                    UnsafeNativeMethods.libssh2_trace_sethandler(
                        this.sessionHandle,
                        IntPtr.Zero,
                        this.TraceHandlerDelegate);

                    UnsafeNativeMethods.libssh2_trace(
                        this.sessionHandle,
                        mask);
                }
            }
        }

        //---------------------------------------------------------------------
        // Dispose.
        //---------------------------------------------------------------------

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                lock (this.sessionHandle.SyncRoot)
                {
                    UnsafeNativeMethods.libssh2_trace_sethandler(
                        this.sessionHandle,
                        IntPtr.Zero,
                        null);

                    this.sessionHandle.Dispose();
                    this.disposed = true;
                }
            }
        }
    }
}