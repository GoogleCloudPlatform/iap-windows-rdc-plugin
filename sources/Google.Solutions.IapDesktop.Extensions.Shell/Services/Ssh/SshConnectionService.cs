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

using Google.Solutions.IapDesktop.Application.ObjectModel;
using Google.Solutions.IapDesktop.Application.Services.Adapters;
using Google.Solutions.IapDesktop.Application.Services.Integration;
using Google.Solutions.IapDesktop.Application.Views;
using Google.Solutions.IapDesktop.Application.Views.ProjectExplorer;
using Google.Solutions.IapDesktop.Extensions.Shell.Services.Adapter;
using Google.Solutions.IapDesktop.Extensions.Shell.Services.ConnectionSettings;
using Google.Solutions.IapDesktop.Extensions.Shell.Services.Tunnel;
using Google.Solutions.IapDesktop.Extensions.Shell.Views.SshTerminal;
using Google.Solutions.IapTunneling.Iap;
using Google.Solutions.IapTunneling.Net;
using Google.Solutions.Ssh;
using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Google.Solutions.IapDesktop.Extensions.Shell.Services.Ssh
{
    public interface ISshConnectionService
    {
        Task ActivateOrConnectInstanceAsync(
            IProjectExplorerVmInstanceNode vmNode);
    }

    [Service(typeof(ISshConnectionService))]
    public class SshConnectionService : ISshConnectionService
    {
        private readonly IWin32Window window;
        private readonly IJobService jobService;
        private readonly ISshTerminalSessionBroker sessionBroker;
        private readonly ITunnelBrokerService tunnelBroker;
        private readonly IConnectionSettingsService settingsService;
        private readonly IAuthorizedKeyService authorizedKeyService;
        private readonly IKeyStoreAdapter keyStoreAdapter;
        private readonly IAuthorizationAdapter authorizationAdapter;
        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        public SshConnectionService(IServiceProvider serviceProvider)
        {
            this.jobService = serviceProvider.GetService<IJobService>();
            this.sessionBroker = serviceProvider.GetService<ISshTerminalSessionBroker>();
            this.tunnelBroker = serviceProvider.GetService<ITunnelBrokerService>();
            this.settingsService = serviceProvider.GetService<IConnectionSettingsService>();
            this.authorizedKeyService = serviceProvider.GetService<IAuthorizedKeyService>();
            this.keyStoreAdapter = serviceProvider.GetService<IKeyStoreAdapter>();
            this.authorizationAdapter = serviceProvider.GetService<IAuthorizationAdapter>();
            this.window = serviceProvider.GetService<IMainForm>().Window;
        }

        //---------------------------------------------------------------------
        // ISshConnectionService.
        //---------------------------------------------------------------------

        public async Task ActivateOrConnectInstanceAsync(IProjectExplorerVmInstanceNode vmNode)
        {
            Debug.Assert(vmNode.IsSshSupported());

            if (this.sessionBroker.TryActivate(vmNode.Reference))
            {
                // SSH session was active, nothing left to do.
                return;
            }

            // Select node so that tracking windows are updated.
            vmNode.Select();

            var instance = vmNode.Reference;
            var settings = (InstanceConnectionSettings)this.settingsService
                .GetConnectionSettings(vmNode)
                .TypedCollection;
            var timeout = TimeSpan.FromSeconds(settings.SshConnectionTimeout.IntValue);

            //
            // Start job to create IAP tunnel.
            //

            var tunnelTask = this.jobService.RunInBackground(
                new JobDescription(
                    $"Opening Cloud IAP tunnel to {instance.Name}...",
                    JobUserFeedbackType.BackgroundFeedback),
                async token =>
                {
                    try
                    {
                        var destination = new TunnelDestination(
                            vmNode.Reference,
                            (ushort)settings.SshPort.IntValue);

                        // NB. Give IAP the same timeout for probing as SSH itself.
                        return await this.tunnelBroker.ConnectAsync(
                                destination,
                                new SameProcessRelayPolicy(),
                                timeout)
                            .ConfigureAwait(false);
                    }
                    catch (NetworkStreamClosedException e)
                    {
                        throw new ConnectionFailedException(
                            "Connecting to the instance failed. Make sure that you have " +
                            "configured your firewall rules to permit Cloud IAP access " +
                            $"to {instance.Name}",
                            HelpTopics.CreateIapFirewallRule,
                            e);
                    }
                    catch (UnauthorizedException)
                    {
                        throw new ConnectionFailedException(
                            "You are not authorized to connect to this VM instance.\n\n" +
                            $"Verify that the Cloud IAP API is enabled in the project {instance.ProjectId} " +
                            "and that your user has the 'IAP-secured Tunnel User' role.",
                            HelpTopics.IapAccess);
                    }
                });


            //
            // Load persistent CNG key. This must be done on the UI thread.
            //
            var email = this.authorizationAdapter.Authorization.Email;
            var rsaKey = this.keyStoreAdapter.CreateRsaKey(
                    $"IAPDESKTOP_{email}",
                    CngKeyUsages.Signing,
                    true,
                    this.window);
            Debug.Assert(rsaKey != null);

            //
            // Start job to publish key, using whatever mechanism is appropriate
            // for this instance.
            //

            var sshKey = new RsaSshKey(rsaKey);
            try
            {
                var authorizedKeyTask = this.jobService.RunInBackground(
                    new JobDescription(
                        $"Publishing SSH key for {instance.Name}...",
                        JobUserFeedbackType.BackgroundFeedback),
                    async token =>
                    {
                        //
                        // Authorize the key.
                        //
                        return await this.authorizedKeyService.AuthorizeKeyAsync(
                                vmNode.Reference,
                                sshKey,
                                TimeSpan.FromDays(30),  // TODO: Make expiry configurable
                                NullIfEmpty(settings.SshUsername.StringValue),
                                AuthorizeKeyMethods.All,
                                token)
                            .ConfigureAwait(true);
                    });

                //
                // Wait for both jobs to continue (they are both fairly slow).
                //

                await Task.WhenAll(tunnelTask, authorizedKeyTask)
                    .ConfigureAwait(true);

                //
                // NB. ConnectAsync takes ownership of the key and will retain
                // it for the lifetime of the session.
                //
                await this.sessionBroker.ConnectAsync(
                        instance,
                        new IPEndPoint(IPAddress.Loopback, tunnelTask.Result.LocalPort),
                        authorizedKeyTask.Result,
                        timeout)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                sshKey.Dispose();
                throw;
            }
        }
    }
}
