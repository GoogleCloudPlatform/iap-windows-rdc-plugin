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

using Google.Solutions.Common.ApiExtensions;
using Google.Solutions.Common.Diagnostics;
using Google.Solutions.Common.Locator;
using Google.Solutions.Common.Util;
using Google.Solutions.IapDesktop.Application;
using Google.Solutions.IapDesktop.Application.Services.Adapters;
using Google.Solutions.IapDesktop.Application.Views.ProjectExplorer;
using Google.Solutions.IapDesktop.Extensions.Os.Inventory;
using Google.Solutions.IapDesktop.Extensions.Os.Services.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Solutions.IapDesktop.Extensions.Os.Views.PackageInventory
{
    public class PackageInventoryModel
    {
        public bool IsInventoryDataAvailable { get; }
        public string DisplayName { get; }
        public IEnumerable<PackageInventoryModel.Item> Packages { get; }

        private PackageInventoryModel(
            string displayName,
            bool isInventoryDataAvailable,
            IEnumerable<PackageInventoryModel.Item> packages)
        {
            this.DisplayName = displayName;
            this.IsInventoryDataAvailable = isInventoryDataAvailable;
            this.Packages = packages;
        }

        private static PackageInventoryModel FromInventory(
            string displayName,
            PackageInventoryType inventoryType,
            IEnumerable<GuestOsInfo> inventory)
        {
            switch (inventoryType)
            {
                case PackageInventoryType.AvailablePackages:
                    return new PackageInventoryModel(
                        displayName,
                        inventory.Any(),
                        inventory
                            .Where(i => i.AvailablePackages != null)
                            .SelectMany(i => i.AvailablePackages
                                .AllPackages
                                .Select(p => new Item(i.Instance, p))));


                case PackageInventoryType.InstalledPackages:
                    return new PackageInventoryModel(
                        displayName,
                        inventory.Any(),
                        inventory
                            .Where(i => i.InstalledPackages != null)
                            .SelectMany(i => i.InstalledPackages
                                .AllPackages
                                .Select(p => new Item(i.Instance, p))));

                default:
                    throw new ArgumentException(nameof(inventoryType));

            }
        }

        public static async Task<PackageInventoryModel> LoadAsync(
            IInventoryService inventoryService,
            PackageInventoryType inventoryType,
            IProjectExplorerNode node,
            CancellationToken token)
        {
            IEnumerable<GuestOsInfo> inventory;
            try
            {
                if (node is IProjectExplorerVmInstanceNode vmNode)
                {
                    var info = await inventoryService.GetInstanceInventoryAsync(
                            vmNode.Reference,
                            token)
                        .ConfigureAwait(false);
                    inventory = info != null
                        ? new GuestOsInfo[] { info }
                        : Enumerable.Empty<GuestOsInfo>();
                }
                else if (node is IProjectExplorerZoneNode zoneNode)
                {
                    inventory = await inventoryService.ListZoneInventoryAsync(
                            new ZoneLocator(zoneNode.ProjectId, zoneNode.ZoneId),
                            OperatingSystems.Windows,
                            token)
                        .ConfigureAwait(false);
                }
                else if (node is IProjectExplorerProjectNode projectNode)
                {
                    inventory = await inventoryService.ListProjectInventoryAsync(
                            projectNode.ProjectId,
                            OperatingSystems.Windows,
                            token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Unknown/unsupported node.
                    return null;
                }
            }
            catch (Exception e) when (e.Unwrap() is GoogleApiException apiEx &&
                apiEx.IsConstraintViolation())
            {
                //
                // Reading OS inventory data can fail because of a 
                // `compute.disableGuestAttributesAccess` constraint.
                //
                ApplicationTraceSources.Default.TraceWarning(
                    "Failed to load OS inventory data: {0}", e);

                inventory = Enumerable.Empty<GuestOsInfo>();
            }

            return PackageInventoryModel.FromInventory(
                node.DisplayName,
                inventoryType,
                inventory);
        }

        public class Item
        {
            public InstanceLocator Instance { get; }
            public IPackage Package { get; }

            public Item(InstanceLocator instance, IPackage package)
            {
                this.Instance = instance;
                this.Package = package;
            }
        }
    }
}
