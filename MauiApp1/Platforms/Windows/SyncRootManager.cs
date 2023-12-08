using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32;

namespace MauiApp1
{
    public class SyncRootManager
    {
        internal static readonly string SyncRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TestCloud");
        internal static readonly Guid StorageProviderId = Guid.Parse("01db0d2c-0e11-4142-89b8-a0472fbab8ae");

        internal const string CloudName = "Test Cloud";
        internal const string StorageProviderAccount = "SomeAccount";

        private unsafe readonly CF_CALLBACK_REGISTRATION[] _callbacks = [
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = new CF_CALLBACK((CF_CALLBACK_INFO* x, CF_CALLBACK_PARAMETERS* y) => CfExecutePlaceholdersFetch(new FileSystemItem
                {
                    Id = Guid.NewGuid(),
                    RelativePath = "testFile.txt",
                    FileAttributes = System.IO.FileAttributes.Normal,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Size = 15654654
                }, x)),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE
            }];

        internal CF_CONNECTION_KEY ConnectionKey { get; private set; }

        public async Task Connect()
        {
            EnsureFeatureSupported();
            EnsureSyncRootPathCreated();

            StorageProviderSyncRootManager.Register(await CreateSyncRoot());

            unsafe
            {
                PInvoke.CfConnectSyncRoot(
                    SyncRootPath,
                    _callbacks,
                    (void*)0,
                    CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
                    out var connectionKey);

                ConnectionKey = connectionKey;

                PInvoke.CfUpdateSyncProviderStatus(ConnectionKey, CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
            }
        }

        private static void EnsureFeatureSupported()
        {
            if (!StorageProviderSyncRootManager.IsSupported())
            {
                throw new NotSupportedException("Cloud API is not supported on this machine.");
            }
        }

        private static void EnsureSyncRootPathCreated()
        {
            if (!Directory.Exists(SyncRootPath))
            {
                Directory.CreateDirectory(SyncRootPath);
            }
        }

        private static async Task<StorageProviderSyncRootInfo> CreateSyncRoot()
        {
            StorageProviderSyncRootInfo syncRoot = new()
            {
                Id = GetSyncRootId(),
                ProviderId = StorageProviderId,
                Path = await StorageFolder.GetFolderFromPathAsync(SyncRootPath),
                AllowPinning = true,
                DisplayNameResource = CloudName,
                HardlinkPolicy = StorageProviderHardlinkPolicy.Allowed,
                HydrationPolicy = StorageProviderHydrationPolicy.Partial,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed | StorageProviderHydrationPolicyModifier.StreamingAllowed,
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
                PopulationPolicy = StorageProviderPopulationPolicy.Full,
                ProtectionMode = StorageProviderProtectionMode.Unknown,
                Version = "1.0.0",
                IconResource = "%SystemRoot%\\system32\\charmap.exe,0",
                ShowSiblingsAsGroup = false,
                RecycleBinUri = null,
                Context = CryptographicBuffer.ConvertStringToBinary(GetSyncRootId(), BinaryStringEncoding.Utf8)
            };

            return syncRoot;

        }

        private static string GetSyncRootId()
            => $"{StorageProviderId}!{WindowsIdentity.GetCurrent().User}!{StorageProviderAccount}";

        #region Placeholder creation
        private unsafe static void CfExecutePlaceholdersFetch(FileSystemItem placeholder, CF_CALLBACK_INFO* callbackInfo)
        {
            CF_OPERATION_INFO operationInfo = CreateOperationInfo(callbackInfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS);
            CF_PLACEHOLDER_CREATE_INFO[] placeholdersArray = GC.AllocateArray<CF_PLACEHOLDER_CREATE_INFO>(1, true);
            placeholdersArray[0] = CreatePlaceholder(placeholder);

            fixed (CF_PLACEHOLDER_CREATE_INFO* arrayPtr = placeholdersArray)
            {
                CF_OPERATION_PARAMETERS opParams = new CF_OPERATION_PARAMETERS();

                opParams.Anonymous.TransferPlaceholders.PlaceholderArray = arrayPtr;
                opParams.Anonymous.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;
                opParams.Anonymous.TransferPlaceholders.PlaceholderCount = 1;
                opParams.Anonymous.TransferPlaceholders.CompletionStatus = new NTSTATUS(0);
                opParams.Anonymous.TransferPlaceholders.PlaceholderTotalCount = 1;
                opParams.ParamSize = (uint)Marshal.SizeOf(opParams);

                HRESULT result = PInvoke.CfExecute(operationInfo, ref opParams);
                result.ThrowOnFailure();
            }
        }

        private unsafe static CF_PLACEHOLDER_CREATE_INFO CreatePlaceholder(FileSystemItem placeholder)
        {
            fixed(void* fileIdentityPtr = "a")
            fixed(char* relativeFileName = placeholder.RelativePath.ToCharArray())
            {
                CF_PLACEHOLDER_CREATE_INFO cfInfo = new()
                {
                    FileIdentity = fileIdentityPtr,
                    FileIdentityLength = 2 * 2,
                    RelativeFileName = relativeFileName,
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = placeholder.Size,
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            FileAttributes = (uint)placeholder.FileAttributes,
                            CreationTime = placeholder.CreationTime.ToFileTime(),
                            LastWriteTime = placeholder.LastWriteTime.ToFileTime(),
                            LastAccessTime = placeholder.LastAccessTime.ToFileTime(),
                            ChangeTime = placeholder.LastWriteTime.ToFileTime()
                        }
                    },
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                };

                return cfInfo;
            }
        }

        private unsafe static CF_OPERATION_INFO CreateOperationInfo(CF_CALLBACK_INFO* CallbackInfo, CF_OPERATION_TYPE OperationType)
        {
            CF_OPERATION_INFO opInfo = new()
            {
                Type = OperationType,
                ConnectionKey = (*CallbackInfo).ConnectionKey,
                TransferKey = (*CallbackInfo).TransferKey,
                CorrelationVector = (*CallbackInfo).CorrelationVector,
                RequestKey = (*CallbackInfo).RequestKey
            };

            opInfo.StructSize = (uint)Marshal.SizeOf(opInfo);
            return opInfo;
        }

        public class FileSystemItem
        {
            public Guid Id { get; set; }
            public string RelativePath { get; set; }
            public long Size { get; set; }
            public System.IO.FileAttributes FileAttributes { get; set; }
            public DateTimeOffset CreationTime { get; set; }
            public DateTimeOffset LastWriteTime { get; set; }
            public DateTimeOffset LastAccessTime { get; set; }
        }
        #endregion
    }
}
