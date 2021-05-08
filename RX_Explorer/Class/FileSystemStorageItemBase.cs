﻿using RX_Explorer.Interface;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using ColorHelper = Microsoft.Toolkit.Uwp.Helpers.ColorHelper;

namespace RX_Explorer.Class
{
    /// <summary>
    /// 提供对设备中的存储对象的描述
    /// </summary>
    public abstract class FileSystemStorageItemBase : IStorageItemPropertiesBase, INotifyPropertyChanged, IStorageItemOperation, IEquatable<FileSystemStorageItemBase>
    {
        public string Path { get; protected set; }

        public virtual string Size { get; }

        public virtual string Name
        {
            get
            {
                return System.IO.Path.GetFileName(Path);
            }
        }

        public virtual string DisplayName
        {
            get
            {
                return Name;
            }
        }

        public virtual string Type
        {
            get
            {
                return System.IO.Path.GetExtension(Path).ToUpper();
            }
        }

        public virtual string DisplayType
        {
            get
            {
                return Type;
            }
        }

        public SolidColorBrush BackgroundColor
        {
            get
            {
                return backgroundColor ??= new SolidColorBrush(Colors.Transparent);
            }
            private set
            {
                backgroundColor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundColor)));
            }
        }

        private SolidColorBrush backgroundColor;

        public void SetColorAsSpecific(Color Color)
        {
            BackgroundColor = new SolidColorBrush(Color);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundColor)));
        }

        public void SetColorAsNormal()
        {
            BackgroundColor = new SolidColorBrush(Colors.Transparent);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundColor)));
        }

        public double ThumbnailOpacity { get; protected set; } = 1d;

        public ulong SizeRaw { get; protected set; }

        public DateTimeOffset CreationTimeRaw { get; protected set; }

        public DateTimeOffset ModifiedTimeRaw { get; protected set; }

        public virtual string ModifiedTime
        {
            get
            {
                if (ModifiedTimeRaw == DateTimeOffset.MaxValue.ToLocalTime() || ModifiedTimeRaw == DateTimeOffset.MinValue.ToLocalTime())
                {
                    return Globalization.GetString("UnknownText");
                }
                else
                {
                    return ModifiedTimeRaw.ToString("G");
                }
            }
        }

        public virtual string CreationTime
        {
            get
            {
                if (CreationTimeRaw == DateTimeOffset.MaxValue.ToLocalTime())
                {
                    return Globalization.GetString("UnknownText");
                }
                else
                {
                    return CreationTimeRaw.ToString("G");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public virtual BitmapImage Thumbnail { get; protected set; }

        public virtual bool IsReadOnly { get; protected set; }

        public virtual bool IsSystemItem { get; protected set; }

        protected static readonly Uri Const_Folder_Image_Uri = new Uri("ms-appx:///Assets/FolderIcon.png");

        protected static readonly Uri Const_File_White_Image_Uri = new Uri("ms-appx:///Assets/Page_Solid_White.png");

        protected static readonly Uri Const_File_Black_Image_Uri = new Uri("ms-appx:///Assets/Page_Solid_Black.png");

        public static async Task<bool> CheckExistAsync(string Path)
        {
            if (System.IO.Path.IsPathRooted(Path))
            {
                if (WIN_Native_API.CheckLocationAvailability(System.IO.Path.GetDirectoryName(Path)))
                {
                    return WIN_Native_API.CheckExist(Path);
                }
                else
                {
                    try
                    {
                        string DirectoryPath = System.IO.Path.GetDirectoryName(Path);

                        if (string.IsNullOrEmpty(DirectoryPath))
                        {
                            await StorageFolder.GetFolderFromPathAsync(Path);
                            return true;
                        }
                        else
                        {
                            StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(DirectoryPath);

                            if (await Folder.TryGetItemAsync(System.IO.Path.GetFileName(Path)) != null)
                            {
                                return true;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, "CheckExist threw an exception");
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }
        }

        public static async Task<FileSystemStorageItemBase> CreateFromStorageItemAsync<T>(T Item) where T : IStorageItem
        {
            switch (Item)
            {
                case StorageFile File:
                    {
                        foreach (ConstructorInfo Info in typeof(FileSystemStorageFile).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            ParameterInfo[] Parameters = Info.GetParameters();

                            if (Parameters[0].ParameterType == typeof(StorageFile))
                            {
                                return (FileSystemStorageFile)Info.Invoke(new object[] { File, await File.GetModifiedTimeAsync(), await File.GetSizeRawDataAsync() });
                            }
                        }

                        return null;
                    }
                case StorageFolder Folder:
                    {
                        foreach (ConstructorInfo Info in typeof(FileSystemStorageFolder).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            ParameterInfo[] Parameters = Info.GetParameters();

                            if (Parameters[0].ParameterType == typeof(StorageFolder))
                            {
                                return (FileSystemStorageFolder)Info.Invoke(new object[] { Folder, await Folder.GetModifiedTimeAsync() });
                            }
                        }

                        return null;
                    }
                default:
                    {
                        return null;
                    }
            }
        }

        public static async Task<FileSystemStorageItemBase> OpenAsync(string Path)
        {
            if (WIN_Native_API.CheckLocationAvailability(System.IO.Path.GetDirectoryName(Path)))
            {
                return WIN_Native_API.GetStorageItem(Path);
            }
            else
            {
                LogTracer.Log($"Native API could not found the path: \"{Path}\", fall back to UWP storage API");

                try
                {
                    string DirectoryPath = System.IO.Path.GetDirectoryName(Path);

                    if (string.IsNullOrEmpty(DirectoryPath))
                    {
                        StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(Path);
                        return await CreateFromStorageItemAsync(Folder);
                    }
                    else
                    {
                        StorageFolder ParentFolder = await StorageFolder.GetFolderFromPathAsync(DirectoryPath);

                        switch (await ParentFolder.TryGetItemAsync(System.IO.Path.GetFileName(Path)))
                        {
                            case StorageFolder Folder:
                                {
                                    return await CreateFromStorageItemAsync(Folder);
                                }
                            case StorageFile File:
                                {
                                    return await CreateFromStorageItemAsync(File);
                                }
                            default:
                                {
                                    LogTracer.Log($"UWP storage API could not found the path: \"{Path}\"");
                                    return null;
                                }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"UWP storage API could not found the path: \"{Path}\"");
                    return null;
                }
            }
        }

        public static async Task<FileSystemStorageItemBase> CreateAsync(string Path, StorageItemTypes ItemTypes, CreateOption Option)
        {
            switch (ItemTypes)
            {
                case StorageItemTypes.File:
                    {
                        if (WIN_Native_API.CreateFileFromPath(Path, Option, out string NewPath))
                        {
                            return await OpenAsync(NewPath);
                        }
                        else
                        {
                            LogTracer.Log($"Native API could not create file: \"{Path}\", fall back to UWP storage API");

                            try
                            {
                                StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(Path));

                                switch (Option)
                                {
                                    case CreateOption.GenerateUniqueName:
                                        {
                                            StorageFile NewFile = await Folder.CreateFileAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.GenerateUniqueName);
                                            return await CreateFromStorageItemAsync(NewFile);
                                        }
                                    case CreateOption.OpenIfExist:
                                        {
                                            StorageFile NewFile = await Folder.CreateFileAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.OpenIfExists);
                                            return await CreateFromStorageItemAsync(NewFile);
                                        }
                                    case CreateOption.ReplaceExisting:
                                        {
                                            StorageFile NewFile = await Folder.CreateFileAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.ReplaceExisting);
                                            return await CreateFromStorageItemAsync(NewFile);
                                        }
                                    default:
                                        {
                                            return null;
                                        }
                                }
                            }
                            catch
                            {
                                LogTracer.Log($"UWP storage API could not create file: \"{Path}\"");
                                return null;
                            }
                        }
                    }
                case StorageItemTypes.Folder:
                    {
                        if (WIN_Native_API.CreateDirectoryFromPath(Path, Option, out string NewPath))
                        {
                            return await OpenAsync(NewPath);
                        }
                        else
                        {
                            LogTracer.Log($"Native API could not create file: \"{Path}\", fall back to UWP storage API");

                            try
                            {
                                StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(Path));

                                switch (Option)
                                {
                                    case CreateOption.GenerateUniqueName:
                                        {
                                            StorageFolder NewFolder = await Folder.CreateFolderAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.GenerateUniqueName);
                                            return await CreateFromStorageItemAsync(NewFolder);
                                        }
                                    case CreateOption.OpenIfExist:
                                        {
                                            StorageFolder NewFolder = await Folder.CreateFolderAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.OpenIfExists);
                                            return await CreateFromStorageItemAsync(NewFolder);
                                        }
                                    case CreateOption.ReplaceExisting:
                                        {
                                            StorageFolder NewFolder = await Folder.CreateFolderAsync(System.IO.Path.GetFileName(Path), CreationCollisionOption.ReplaceExisting);
                                            return await CreateFromStorageItemAsync(NewFolder);
                                        }
                                    default:
                                        {
                                            return null;
                                        }
                                }
                            }
                            catch
                            {
                                LogTracer.Log($"UWP storage API could not create folder: \"{Path}\"");
                                return null;
                            }
                        }
                    }
                default:
                    {
                        return null;
                    }
            }
        }

        protected FileSystemStorageItemBase(string Path)
        {
            this.Path = Path;
        }

        protected FileSystemStorageItemBase(string Path, WIN_Native_API.WIN32_FIND_DATA Data)
        {
            this.Path = Path;

            if (Data != default)
            {
                IsReadOnly = ((System.IO.FileAttributes)Data.dwFileAttributes).HasFlag(System.IO.FileAttributes.ReadOnly);
                IsSystemItem = IsReadOnly = ((System.IO.FileAttributes)Data.dwFileAttributes).HasFlag(System.IO.FileAttributes.System);

                SizeRaw = ((ulong)Data.nFileSizeHigh << 32) + Data.nFileSizeLow;

                WIN_Native_API.FileTimeToSystemTime(ref Data.ftLastWriteTime, out WIN_Native_API.SYSTEMTIME ModTime);
                ModifiedTimeRaw = new DateTime(ModTime.Year, ModTime.Month, ModTime.Day, ModTime.Hour, ModTime.Minute, ModTime.Second, ModTime.Milliseconds, DateTimeKind.Utc).ToLocalTime();

                WIN_Native_API.FileTimeToSystemTime(ref Data.ftCreationTime, out WIN_Native_API.SYSTEMTIME CreTime);
                CreationTimeRaw = new DateTime(CreTime.Year, CreTime.Month, CreTime.Day, CreTime.Hour, CreTime.Minute, CreTime.Second, CreTime.Milliseconds, DateTimeKind.Utc).ToLocalTime();
            }
        }

        protected void OnPropertyChanged(string Name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Name));
        }

        public virtual void SetThumbnailOpacity(ThumbnailStatus Status)
        {
            switch (Status)
            {
                case ThumbnailStatus.Normal:
                    {
                        if (ThumbnailOpacity != 1d)
                        {
                            ThumbnailOpacity = 1d;
                        }
                        break;
                    }
                case ThumbnailStatus.ReduceOpacity:
                    {
                        if (ThumbnailOpacity != 0.5)
                        {
                            ThumbnailOpacity = 0.5;
                        }
                        break;
                    }
            }

            OnPropertyChanged(nameof(ThumbnailOpacity));
        }

        public async Task LoadMorePropertiesAsync()
        {
            if (!CheckIfPropertiesLoaded())
            {
                if ((this is FileSystemStorageFile && SettingControl.ContentLoadMode == LoadMode.OnlyFile) || SettingControl.ContentLoadMode == LoadMode.FileAndFolder)
                {
                    await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
                    {
                        try
                        {
                            await LoadMorePropertiesCore(false);

                            OnPropertyChanged(nameof(Size));
                            OnPropertyChanged(nameof(Name));
                            OnPropertyChanged(nameof(ModifiedTime));
                            OnPropertyChanged(nameof(Thumbnail));
                            OnPropertyChanged(nameof(DisplayType));
                        }
                        catch (Exception ex)
                        {
                            LogTracer.Log(ex, $"An exception was threw in {nameof(LoadMorePropertiesAsync)}, StorageType: {GetType().FullName}, Path: {Path}");
                        }
                    });
                }

                await LoadForegroundConfiguration();
            }
        }

        private async Task LoadForegroundConfiguration()
        {
            string ColorString = await SQLite.Current.GetFileColorAsync(Path);

            if (!string.IsNullOrEmpty(ColorString))
            {
                SetColorAsSpecific(ColorHelper.ToColor(ColorString));
            }
        }

        protected abstract bool CheckIfPropertiesLoaded();

        protected abstract Task LoadMorePropertiesCore(bool ForceUpdate);

        public abstract Task<IStorageItem> GetStorageItemAsync();

        public async Task RefreshAsync()
        {
            try
            {
                if (await CheckExistAsync(Path))
                {
                    await LoadMorePropertiesCore(true);

                    OnPropertyChanged(nameof(Size));
                    OnPropertyChanged(nameof(Name));
                    OnPropertyChanged(nameof(ModifiedTime));
                    OnPropertyChanged(nameof(Thumbnail));
                    OnPropertyChanged(nameof(DisplayType));
                }
                else
                {
                    LogTracer.Log($"File/Folder not found or access deny when executing FileSystemStorageItemBase.Update, path: {Path}");
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"An exception was threw when executing FileSystemStorageItemBase.Update, path: {Path}");
            }
        }

        public virtual async Task MoveAsync(string DirectoryPath, ProgressChangedEventHandler ProgressHandler = null, bool IsUndoOperation = false)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                await Exclusive.Controller.MoveAsync(Path, DirectoryPath, IsUndoOperation, ProgressHandler);
            }
        }

        public virtual Task MoveAsync(FileSystemStorageFolder Directory, ProgressChangedEventHandler ProgressHandler = null, bool IsUndoOperation = false)
        {
            return MoveAsync(Directory.Path, ProgressHandler, IsUndoOperation);
        }

        public virtual async Task CopyAsync(string DirectoryPath, ProgressChangedEventHandler ProgressHandler = null, bool IsUndoOperation = false)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                await Exclusive.Controller.CopyAsync(Path, DirectoryPath, IsUndoOperation, ProgressHandler);
            }
        }

        public virtual Task CopyAsync(FileSystemStorageFolder Directory, ProgressChangedEventHandler ProgressHandler = null, bool IsUndoOperation = false)
        {
            return CopyAsync(Directory.Path, ProgressHandler, IsUndoOperation);
        }

        public async virtual Task<string> RenameAsync(string DesireName)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                string NewName = await Exclusive.Controller.RenameAsync(Path, DesireName);
                Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), NewName);
                return NewName;
            }
        }

        public virtual async Task DeleteAsync(bool PermanentDelete, ProgressChangedEventHandler ProgressHandler = null, bool IsUndoOperation = false)
        {
            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
            {
                await Exclusive.Controller.DeleteAsync(Path, PermanentDelete, IsUndoOperation, ProgressHandler);
            }
        }

        public override string ToString()
        {
            return Name;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            else
            {
                if (obj is FileSystemStorageItemBase Item)
                {
                    return Item.Path.Equals(Path, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    return false;
                }
            }
        }

        public override int GetHashCode()
        {
            return Path.GetHashCode();
        }

        public bool Equals(FileSystemStorageItemBase other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            else
            {
                if (other == null)
                {
                    return false;
                }
                else
                {
                    return other.Path.Equals(Path, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static bool operator ==(FileSystemStorageItemBase left, FileSystemStorageItemBase right)
        {
            if (left is null)
            {
                return right is null;
            }
            else
            {
                if (right is null)
                {
                    return false;
                }
                else
                {
                    return left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static bool operator !=(FileSystemStorageItemBase left, FileSystemStorageItemBase right)
        {
            if (left is null)
            {
                return right is object;
            }
            else
            {
                if (right is null)
                {
                    return true;
                }
                else
                {
                    return !left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
