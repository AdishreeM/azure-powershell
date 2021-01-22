﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using Microsoft.Azure.Commands.Common.Authentication;
using Microsoft.Azure.Commands.Common.Authentication.Abstractions;
using Microsoft.Azure.Commands.Compute.Models;
using Microsoft.Azure.Commands.ResourceManager.Common.ArgumentCompleters;
using Microsoft.Azure.Management.Storage.Version2017_10_01;
using Microsoft.WindowsAzure.Commands.Sync.Download;
using Microsoft.WindowsAzure.Commands.Tools.Vhd;
using Microsoft.WindowsAzure.Commands.Tools.Vhd.Model;
using System;
using System.IO;
using System.Management.Automation;
using Rsrc = Microsoft.Azure.Commands.Compute.Properties.Resources;
using Microsoft.Azure.Management.Compute.Models;
using Microsoft.Azure.Commands.Compute.Automation.Models;
using Microsoft.WindowsAzure.Commands.Utilities.Common;
using Microsoft.Azure.Commands.Compute.Automation;
using Microsoft.Azure.Management.Compute;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Azure.Commands.Compute.Sync.Upload;
using Microsoft.WindowsAzure.Commands.Sync;
using System.Management;
using Microsoft.Samples.HyperV.Storage;
using Microsoft.Samples.HyperV.Common;

namespace Microsoft.Azure.Commands.Compute.StorageServices
{
    /// <summary>
    /// Uploads a vhd as fixed disk format vhd to a blob in Microsoft Azure Storage
    /// </summary>
    [Cmdlet("Add", ResourceManager.Common.AzureRMConstants.AzureRMPrefix + "Vhd"), OutputType(typeof(VhdUploadContext))]
    public class AddAzureVhdCommand : ComputeClientBaseCmdlet
    {
        private const int DefaultNumberOfUploaderThreads = 8;
        private const string DefaultParameterSet = "DefaultParameterSet";
        private const string DirectUploadToManagedDiskSet = "DirectUploadToManagedDiskSet";

        [Parameter(
            Position = 0,
            Mandatory = false,
            ParameterSetName = DefaultParameterSet,
            ValueFromPipelineByPropertyName = true)]
        [Parameter(
            Position = 0,
            Mandatory = true,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true)]
        [ResourceGroupCompleter]
        [ValidateNotNullOrEmpty]
        public string ResourceGroupName { get; set; }

        [Parameter(
            Position = 1,
            ParameterSetName = DefaultParameterSet,
            Mandatory = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Uri to blob")]
        [ValidateNotNullOrEmpty]
        [Alias("dst")]
        public Uri Destination
        {
            get;
            set;
        }

        [Parameter(
            Position = 2,
            Mandatory = true,
            ParameterSetName = DefaultParameterSet,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Local path of the vhd file")]
        [Parameter(
            Position = 2,
            Mandatory = true,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Local path of the vhd file")]
        [ValidateNotNullOrEmpty]
        [Alias("lf")]
        public FileInfo LocalFilePath
        {
            get;
            set;
        }

        [Parameter(
            Mandatory = true,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Name of the new managed Disk")]
        [ValidateNotNullOrEmpty]
        public string DiskName { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Location of new Managed Disk")]
        [LocationCompleter("Microsoft.Compute/disks")]
        public string Location { get; set; }

        [Parameter(
            Mandatory = false,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Sku for managed disk. Options: Standard_LRS, Premium_LRS, StandardSSD_LRS, UltraSSD_LRS")]
        [PSArgumentCompleter("Standard_LRS", "Premium_LRS", "StandardSSD_LRS", "UltraSSD_LRS")]
        public string DiskSku { get; set; }

        [Parameter(
            Mandatory = false,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "OS Type for managed disk. Windows or Linux")]
        [PSArgumentCompleter("Windows", "Linux")]
        public OperatingSystemTypes? DiskOsType { get; set; }

        [Parameter(
            Mandatory = false,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true)]
        public int DiskSizeGB { get; set; }

        [Parameter(
            Mandatory = false,
            ParameterSetName = DirectUploadToManagedDiskSet,
            ValueFromPipelineByPropertyName = true)]
        public string[] Zone { get; set; }

        [Parameter(
            Position = 3,
            Mandatory = false,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Number of uploader threads")]
        [ValidateNotNullOrEmpty]
        [ValidateRange(1, 64)]
        [Alias("th")]
        public int? NumberOfUploaderThreads
        {
            get;
            set;
        }

        [Parameter(
            Position = 4,
            Mandatory = false,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Uri to a base image in a blob storage account to apply the difference")]
        [ValidateNotNullOrEmpty]
        [Alias("bs")]
        public Uri BaseImageUriToPatch
        {
            get;
            set;
        }

        [Parameter(
            Position = 5,
            Mandatory = false,
            ValueFromPipelineByPropertyName = true,
            ParameterSetName = "Vhd",
            HelpMessage = "Delete the blob if already exists")]
        [ValidateNotNullOrEmpty]
        [Alias("o")]
        public SwitchParameter OverWrite
        {
            get;
            set;
        }

        [Parameter(Mandatory = false, HelpMessage = "Run cmdlet in the background")]
        public SwitchParameter AsJob { get; set; }

        public UploadParameters ValidateParameters()
        {
            // validate destination
            BlobUri destinationUri;
            if (!BlobUri.TryParseUri(Destination, out destinationUri))
            {
                throw new ArgumentOutOfRangeException("Destination", this.Destination.ToString());
            }

            // validate baseImageUriToPatch
            BlobUri baseImageUri = null;
            if (this.BaseImageUriToPatch != null)
            {
                if (!BlobUri.TryParseUri(BaseImageUriToPatch, out baseImageUri))
                {
                    throw new ArgumentOutOfRangeException("BaseImageUriToPatch", this.BaseImageUriToPatch.ToString());
                }

                if (!String.IsNullOrEmpty(destinationUri.Uri.Query))
                {
                    var message = String.Format(Rsrc.AddAzureVhdCommandSASUriNotSupportedInPatchMode, destinationUri.Uri);
                    throw new ArgumentOutOfRangeException("Destination", message);
                }
            }

            var storageCredentialsFactory = CreateStorageCredentialsFactory();


            // sets objects, no create yet
            PathIntrinsics currentPath = SessionState.Path;
            var filePath = new FileInfo(currentPath.GetUnresolvedProviderPathFromPSPath(LocalFilePath.ToString()));
            var parameters = new UploadParameters(
                destinationUri, baseImageUri, filePath, OverWrite.IsPresent,
                (NumberOfUploaderThreads) ?? DefaultNumberOfUploaderThreads)
            {
                Cmdlet = this,
                BlobObjectFactory = new CloudPageBlobObjectFactory(storageCredentialsFactory, TimeSpan.FromMinutes(1))
            };

            return parameters;
        }

        private StorageCredentialsFactory CreateStorageCredentialsFactory()
        {
            StorageCredentialsFactory storageCredentialsFactory;

            var storageClient = AzureSession.Instance.ClientFactory.CreateArmClient<StorageManagementClient>(
                        DefaultProfile.DefaultContext, AzureEnvironment.Endpoint.ResourceManager);

            if (StorageCredentialsFactory.IsChannelRequired(Destination))
            {
                storageCredentialsFactory = new StorageCredentialsFactory(this.ResourceGroupName, storageClient, DefaultContext.Subscription);
            }
            else
            {
                storageCredentialsFactory = new StorageCredentialsFactory();
            }

            return storageCredentialsFactory;
        }

        public override void ExecuteCmdlet()
        {
            base.ExecuteCmdlet();
            ExecuteClientAction(() =>
            {

                // 1.              CONVERT VHDX TO VHD
                if (this.LocalFilePath.Extension == ".vhdx")
                {
                    convertVhd();
                }

                checkForCorruptedAndDynamicallySizedVhd();
                checkVhdFileSize(this.LocalFilePath); 

                // 2.            RESIZE VHD
                if ((this.LocalFilePath.Length - 512) % 1048576 != 0)
                {
                    resizeVhdFile();
                }
                else // does not need resizing
                {
                    WriteVerbose("Vhd file already sized correctly. Proceeding to uploading.");
                }



                if (this.ParameterSetName == DirectUploadToManagedDiskSet)
                {

                    // 3. DIRECT UPLOAD TO MANAGED DISK

                    // 3-1. CREATE DISK CONFIG 
                    checkForExistingDisk(this.ResourceGroupName, this.DiskName);
                    var diskConfig = CreateDiskConfig();

                    // 3-2: CREATE DISK
                    createManagedDisk(this.ResourceGroupName, this.DiskName, diskConfig);

                    // 3-3: GENERATE SAS
                    GrantAzureRmDiskAccess sas = new GrantAzureRmDiskAccess();
                    var grantAccessData = new GrantAccessData();
                    grantAccessData.Access = "Write";
                    long gbInBytes = 1073741824;
                    int gb = (int)(this.LocalFilePath.Length / gbInBytes);
                    grantAccessData.DurationInSeconds = 86400 * Math.Max(gb / 100, 1);   // 24h per 100gb
                    var accessUri = sas.DisksClient.GrantAccess(this.ResourceGroupName, this.DiskName, grantAccessData);
                    Uri sasUri = new Uri(accessUri.AccessSAS);

                    // 3-4: UPLOAD                  

                    PageBlobClient managedDisk = new PageBlobClient(sasUri, null);
                    Program.SyncOutput = new PSSyncOutputEvents(this);
                    DiskUploadCreator diskUploadCreator = new DiskUploadCreator();
                    var uploadContext = diskUploadCreator.Create(this.LocalFilePath, managedDisk, false);
                    var synchronizer = new DiskSynchronizer(uploadContext, this.NumberOfUploaderThreads ?? DefaultNumberOfUploaderThreads);

                    if (synchronizer.Synchronize())
                    {
                        var result = new VhdUploadContext { LocalFilePath = this.LocalFilePath, DestinationUri = sasUri };
                        WriteObject(result);
                    }
                    else
                    {
                        Console.WriteLine("It has failed");
                    }


                    // 3-5: REVOKE SAS
                    RevokeAzureRmDiskAccess revokeSas = new RevokeAzureRmDiskAccess();
                    var RevokeResult = revokeSas.DisksClient.RevokeAccessWithHttpMessagesAsync(this.ResourceGroupName, this.DiskName).GetAwaiter().GetResult();
                    PSOperationStatusResponse output = new PSOperationStatusResponse
                    {
                        StartTime = this.StartTime,
                        EndTime = DateTime.Now
                    };

                    if (RevokeResult != null && RevokeResult.Request != null && RevokeResult.Request.RequestUri != null)
                    {
                        output.Name = GetOperationIdFromUrlString(RevokeResult.Request.RequestUri.ToString());
                    }

                    WriteObject(output);


                }
                else
                {
                    var parameters = ValidateParameters();
                    var vhdUploadContext = VhdUploaderModel.Upload(parameters);
                    WriteObject(vhdUploadContext);
                }

            });


        }

        private PSDisk CreateDiskConfig()
        {

            // Sku
            DiskSku vSku = null;

            // CreationData
            CreationData vCreationData = null;

            if (this.IsParameterBound(c => c.DiskSku))
            {
                if (vSku == null)
                {
                    vSku = new DiskSku();
                }
                vSku.Name = this.DiskSku;
            }

            vCreationData = new CreationData();
            vCreationData.CreateOption = "upload";
            vCreationData.UploadSizeBytes = this.LocalFilePath.Length;

            var vDisk = new PSDisk
            {
                Zones = this.IsParameterBound(c => c.Zone) ? this.Zone : null,
                OsType = this.IsParameterBound(c => c.DiskOsType) ? this.DiskOsType : (OperatingSystemTypes?)null,
                HyperVGeneration = null,
                DiskSizeGB = this.IsParameterBound(c => c.DiskSizeGB) ? this.DiskSizeGB : (int?)null,
                DiskIOPSReadWrite = null,
                DiskMBpsReadWrite = null,
                DiskIOPSReadOnly = null,
                DiskMBpsReadOnly = null,
                MaxShares = null,
                Location = this.IsParameterBound(c => c.Location) ? this.Location : null,
                Tags = null,
                Sku = vSku,
                CreationData = vCreationData,
                EncryptionSettingsCollection = null,
                Encryption = null,
                NetworkAccessPolicy = null,
                DiskAccessId = null
            };
            return vDisk;
        }

        private void createBackUp(string filePath)
        {
            if (filePath.Contains("_FixedSize"))
            {
                return;
            }

            string backupPath = returnAvailExtensionName(filePath, "_backup", Path.GetExtension(filePath));
            Console.WriteLine("Making a back up copy to: " + backupPath);
            this.LocalFilePath.CopyTo(backupPath);
        }

        public string returnAvailExtensionName(string filePath, string extension, string format)
        {
            string extensionPath = Path.GetDirectoryName(filePath) + @"\" + Path.GetFileNameWithoutExtension(filePath) + extension + format;
            int extNum = 0;
            while (File.Exists(extensionPath))
            {
                extensionPath = Path.GetDirectoryName(filePath) + @"\" + Path.GetFileNameWithoutExtension(filePath) + extension + "("+ extNum + ")" + format;
                extNum += 1;
            }
            return extensionPath;
        }

        private void createManagedDisk(string ResourceGroupName, string DiskName, PSDisk psDisk)
        {
            NewAzureRmDisk newDisk = new NewAzureRmDisk();
            string resourceGroupName = ResourceGroupName;
            string diskName = DiskName;
            Disk disk = new Disk();
            ComputeAutomationAutoMapperProfile.Mapper.Map<PSDisk, Disk>(psDisk, disk);

            var result = newDisk.DisksClient.CreateOrUpdate(resourceGroupName, diskName, disk);
            var psObject = new PSDisk();
            ComputeAutomationAutoMapperProfile.Mapper.Map<Disk, PSDisk>(result, psObject);
            WriteObject(psObject);
        }

        private void checkForCorruptedAndDynamicallySizedVhd()
        {
            // checking for corrupted vhd
            PathIntrinsics currentPath = SessionState.Path;
            var filePath = new FileInfo(currentPath.GetUnresolvedProviderPathFromPSPath(LocalFilePath.ToString()));

            using (var vds = new VirtualDiskStream(filePath.FullName))
            {
                if (vds.DiskType == DiskType.Fixed)
                {
                    long divisor = Convert.ToInt64(Math.Pow(2, 9));
                    long rem = 0;
                    Math.DivRem(filePath.Length, divisor, out rem);
                    if (rem != 0)
                    {
                        throw new ArgumentOutOfRangeException("LocalFilePath", "Given vhd file is a corrupted fixed vhd");
                    }
                }
                else
                {
                    convertDynamicVhdToStatic();
                }
            }
            return;
        }

        private void checkVhdFileSize(FileInfo file)
        {
            if (file.Length < 20971520 || file.Length > 4396972769280)
            {
                throw new Exception("Vhd (Hard Disk Image) file must be between 20 MB and 4095 GB");
            }

            return;
        }

        private void checkForExistingDisk(string resourceGroupName, string DiskName)
        {
            GetAzureRmDisk azDisk = new GetAzureRmDisk();
            Disk aDisk;
            try
            {
                aDisk = azDisk.DisksClient.Get(resourceGroupName, DiskName);
            }
            catch
            {
                aDisk = null;
            }
            if (aDisk != null)
            {
                throw new Exception(string.Format("A Disk with name '{0}' in resource group '{1}' already exists. Please use a different DiskName.", DiskName, resourceGroupName));
            }
        }

        private void convertVhd()
        {
            string ConvertedPath = returnAvailExtensionName(this.LocalFilePath.FullName, "_Converted", ".vhd");
            FileInfo vhdFileInfo = new FileInfo(ConvertedPath);
            ManagementScope scope = new ManagementScope(@"\root\virtualization\V2");
            VirtualHardDiskSettingData settingData = new VirtualHardDiskSettingData(VirtualHardDiskType.FixedSize, VirtualHardDiskFormat.Vhd, vhdFileInfo.FullName, null, 0, 0, 0, 0);

            try
            {
                using (ManagementObject imageManagementService =
                StorageUtilities.GetImageManagementService(scope))
                {
                    Console.WriteLine("Converting VHDX file to VHD file.");
                    using (ManagementBaseObject inParams =
                        imageManagementService.GetMethodParameters("ConvertVirtualHardDisk"))
                    {
                        inParams["SourcePath"] = this.LocalFilePath.FullName;
                        inParams["VirtualDiskSettingData"] =
                            settingData.GetVirtualHardDiskSettingDataEmbeddedInstance(null, imageManagementService.Path.Path);

                        using (ManagementBaseObject outParams = imageManagementService.InvokeMethod(
                            "ConvertVirtualHardDisk", inParams, null))
                        {
                            WmiUtilities.ValidateOutput(outParams, scope);
                        }
                    }
                    Console.WriteLine("Converted file: " + ConvertedPath);
                }
            }
            catch
            {
                Exception ex = new Exception("Failed to convert VHD file to fixed size. Hyper-V is not found.\nFollow this link to enabled Hyper-V or convert file manually: *link*");
                ThrowTerminatingError(new ErrorRecord(
                    ex,
                    "Hyper-V is unavailable",
                    ErrorCategory.InvalidOperation,
                    null));
            }
            this.LocalFilePath = vhdFileInfo;
        }

        private void convertDynamicVhdToStatic()
        {
            string FixedSizePath = returnAvailExtensionName(this.LocalFilePath.FullName, "_FixedSize", ".vhd");
            //string FixedSizePath = this.LocalFilePath.DirectoryName + @"\" + Path.GetFileNameWithoutExtension(this.LocalFilePath.FullName) + "_fixedSize.vhd";
            FileInfo FixedFileInfo = new FileInfo(FixedSizePath);
            ManagementScope scope = new ManagementScope(@"\root\virtualization\V2");
            VirtualHardDiskSettingData settingData = new VirtualHardDiskSettingData(VirtualHardDiskType.FixedSize, VirtualHardDiskFormat.Vhd, FixedFileInfo.FullName, null, 0, 0, 0, 0);
            try
            {
                using (ManagementObject imageManagementService =
                StorageUtilities.GetImageManagementService(scope))
                {
                    Console.WriteLine("Converting dynamically sized VHD to fixed size VHD.");
                    using (ManagementBaseObject inParams =
                        imageManagementService.GetMethodParameters("ConvertVirtualHardDisk"))
                    {
                        inParams["SourcePath"] = this.LocalFilePath.FullName;
                        inParams["VirtualDiskSettingData"] =
                            settingData.GetVirtualHardDiskSettingDataEmbeddedInstance(null, imageManagementService.Path.Path);

                        using (ManagementBaseObject outParams = imageManagementService.InvokeMethod(
                            "ConvertVirtualHardDisk", inParams, null))
                        {
                            WmiUtilities.ValidateOutput(outParams, scope);
                            this.LocalFilePath = FixedFileInfo;
                        }
                        Console.WriteLine("Fixed VHD file: " + FixedSizePath);
                    }
                }
            }
            catch 
            {
                Exception ex = new Exception("Failed to convert VHDX file. Hyper-V is not found.\nFollow this link to enabled Hyper-V or convert file manually: *link*");
                //ERROR MESSAGE
                ThrowTerminatingError(new ErrorRecord(
                    ex,
                    "Hyper-V is unavailable",
                    ErrorCategory.InvalidOperation,
                    null));
            }
            this.LocalFilePath = FixedFileInfo;
        }

        private void resizeVhdFile()
        {
            try
            {
                ManagementScope scope = new ManagementScope(@"\root\virtualization\V2");

                UInt64 FileSize = 1048576 * Convert.ToUInt64(Math.Ceiling((this.LocalFilePath.Length - 512) / 1048576.0));
                UInt64 FullFileSize = FileSize + 512;


                using (ManagementObject imageManagementService =
                    StorageUtilities.GetImageManagementService(scope))
                {
                    Console.WriteLine("Resizing " + this.LocalFilePath);
                    createBackUp(this.LocalFilePath.FullName);
                    using (ManagementBaseObject inParams =
                        imageManagementService.GetMethodParameters("ResizeVirtualHardDisk"))
                    {
                        inParams["Path"] = this.LocalFilePath.FullName;
                        inParams["MaxInternalSize"] = FileSize;
                        using (ManagementBaseObject outParams = imageManagementService.InvokeMethod(
                            "ResizeVirtualHardDisk", inParams, null))
                        {
                            WmiUtilities.ValidateOutput(outParams, scope);
                        }
                    }
                    Console.WriteLine("Resized " + this.LocalFilePath + " from " + this.LocalFilePath.Length + " bytes to " + FullFileSize + " bytes.");
                }
            }
            catch 
            {
                Exception ex = new Exception("Failed to resize VHD file. Hyper-V is not found.\nFollow this link to enabled Hyper-V or convert file manually: *link*");
                //ERROR MESSAGE
                ThrowTerminatingError(new ErrorRecord(
                    ex,
                    "Hyper-V is unavailable",
                    ErrorCategory.InvalidOperation,
                    null));
            }
        }
    }
}
