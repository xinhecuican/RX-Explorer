﻿using Microsoft.Toolkit.Deferred;
using Microsoft.Win32;
using ShareClassLibrary;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Storage;
using Size = System.Drawing.Size;
using Timer = System.Threading.Timer;

namespace FullTrustProcess
{
    class Program
    {
        private static AppServiceConnection Connection;

        private static ManualResetEvent ExitLocker;

        private static Timer AliveCheckTimer;

        private static Process ExplorerProcess;

        private static NamedPipeWriteController PipeCommandWriteController;

        private static NamedPipeReadController PipeCommandReadController;

        private static NamedPipeWriteController PipeProgressWriterController;

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                ExitLocker = new ManualResetEvent(false);

                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                if (args.FirstOrDefault() != "/ExecuteAdminOperation")
                {
                    Connection = new AppServiceConnection
                    {
                        AppServiceName = "CommunicateService",
                        PackageFamilyName = Package.Current.Id.FamilyName
                    };
                    Connection.RequestReceived += Connection_RequestReceived;
                    Connection.ServiceClosed += Connection_ServiceClosed;

                    AppServiceConnectionStatus Status = Connection.OpenAsync().AsTask().Result;

                    if (Status == AppServiceConnectionStatus.Success)
                    {
                        AliveCheckTimer = new Timer(AliveCheck, null, 10000, 10000);

                        try
                        {
                            //Loading the menu in advance can speed up the re-generation speed and ensure the stability of the number of menu items
                            string TempFolderPath = Path.GetTempPath();

                            if (Directory.Exists(TempFolderPath))
                            {
                                ContextMenu.GetContextMenuItems(TempFolderPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogTracer.Log(ex, $"Load menu in advance threw an exception, message: {ex.Message}");
                        }
                    }
                    else
                    {
                        LogTracer.Log($"Could not connect to the appservice. Reason: {Enum.GetName(typeof(AppServiceConnectionStatus), Status)}. Exiting...");
                        ExitLocker.Set();
                    }

                    ExitLocker.WaitOne();
                }
                else
                {
                    string Input = args.LastOrDefault();

                    if (!string.IsNullOrEmpty(Input))
                    {
                        string DataPath = Input.Decrypt("W8aPHu7MGOGA5x5x");

                        if (File.Exists(DataPath))
                        {
                            using (Process CurrentProcess = Process.GetCurrentProcess())
                            {
                                string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{CurrentProcess.Id}");

                                try
                                {
                                    string[] InitData = File.ReadAllLines(DataPath);

                                    using (StreamWriter Writer = File.CreateText(TempFilePath))
                                    {
                                        switch (JsonSerializer.Deserialize(InitData[1], Type.GetType(InitData[0])))
                                        {
                                            case ElevationCreateNewData NewData:
                                                {
                                                    if (StorageController.CheckPermission(NewData.Type == CreateType.File ? FileSystemRights.CreateFiles : FileSystemRights.CreateDirectories, Path.GetDirectoryName(NewData.Path) ?? NewData.Path))
                                                    {
                                                        if (StorageController.Create(NewData.Type, NewData.Path))
                                                        {
                                                            Writer.WriteLine("Success");
                                                        }
                                                        else
                                                        {
                                                            Writer.WriteLine("Error_Failure");
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Writer.WriteLine("Error_NoPermission");
                                                    }

                                                    break;
                                                }
                                            case ElevationCopyData CopyData:
                                                {
                                                    if (CopyData.SourcePath.All((Item) => Directory.Exists(Item) || File.Exists(Item)))
                                                    {
                                                        if (StorageController.CheckPermission(FileSystemRights.Modify, CopyData.DestinationPath))
                                                        {
                                                            List<string> OperationRecordList = new List<string>();

                                                            if (StorageController.Copy(CopyData.SourcePath, CopyData.DestinationPath, CopyData.Option, PostCopyEvent: (se, arg) =>
                                                            {
                                                                if (arg.Result == HRESULT.S_OK)
                                                                {
                                                                    if (arg.DestItem == null || string.IsNullOrEmpty(arg.Name))
                                                                    {
                                                                        OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Copy||{Path.Combine(arg.DestFolder.FileSystemPath, arg.SourceItem.Name)}");
                                                                    }
                                                                    else
                                                                    {
                                                                        OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Copy||{Path.Combine(arg.DestFolder.FileSystemPath, arg.Name)}");
                                                                    }
                                                                }
                                                            }))
                                                            {
                                                                Writer.WriteLine("Success");
                                                                Writer.WriteLine(JsonSerializer.Serialize(OperationRecordList));
                                                            }
                                                            else
                                                            {
                                                                Writer.WriteLine("Error_Failure");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            Writer.WriteLine("Error_NoPermission");
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Writer.WriteLine("Error_NotFound");
                                                    }

                                                    break;
                                                }
                                            case ElevationMoveData MoveData:
                                                {
                                                    if (MoveData.SourcePath.Keys.All((Item) => Directory.Exists(Item) || File.Exists(Item)))
                                                    {
                                                        if (MoveData.SourcePath.Keys.Any((Item) => StorageController.CheckCaptured(Item)))
                                                        {
                                                            Writer.WriteLine("Error_Capture");
                                                        }
                                                        else
                                                        {
                                                            if (StorageController.CheckPermission(FileSystemRights.Modify, MoveData.DestinationPath)
                                                                && MoveData.SourcePath.Keys.All((Path) => StorageController.CheckPermission(FileSystemRights.Modify, System.IO.Path.GetDirectoryName(Path) ?? Path)))
                                                            {
                                                                List<string> OperationRecordList = new List<string>();

                                                                if (StorageController.Move(MoveData.SourcePath, MoveData.DestinationPath, MoveData.Option, PostMoveEvent: (se, arg) =>
                                                                {
                                                                    if (arg.Result == HRESULT.COPYENGINE_S_DONT_PROCESS_CHILDREN)
                                                                    {
                                                                        if (arg.DestItem == null || string.IsNullOrEmpty(arg.Name))
                                                                        {
                                                                            OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Move||{Path.Combine(arg.DestFolder.FileSystemPath, arg.SourceItem.Name)}");
                                                                        }
                                                                        else
                                                                        {
                                                                            OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Move||{Path.Combine(arg.DestFolder.FileSystemPath, arg.Name)}");
                                                                        }
                                                                    }
                                                                }))
                                                                {
                                                                    if (MoveData.SourcePath.Keys.All((Item) => !Directory.Exists(Item) && !File.Exists(Item)))
                                                                    {
                                                                        Writer.WriteLine("Success");
                                                                        Writer.WriteLine(JsonSerializer.Serialize(OperationRecordList));
                                                                    }
                                                                    else
                                                                    {
                                                                        Writer.WriteLine("Error_Capture");
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    Writer.WriteLine("Error_Failure");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                Writer.WriteLine("Error_NoPermission");
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Writer.WriteLine("Error_NotFound");
                                                    }

                                                    break;
                                                }
                                            case ElevationDeleteData DeleteData:
                                                {
                                                    if (DeleteData.DeletePath.All((Item) => Directory.Exists(Item) || File.Exists(Item)))
                                                    {
                                                        if (DeleteData.DeletePath.Any((Item) => StorageController.CheckCaptured(Item)))
                                                        {
                                                            Writer.WriteLine("Error_Capture");
                                                        }
                                                        else
                                                        {
                                                            if (DeleteData.DeletePath.All((Path) => StorageController.CheckPermission(FileSystemRights.Modify, System.IO.Path.GetDirectoryName(Path) ?? Path)))
                                                            {
                                                                List<string> OperationRecordList = new List<string>();

                                                                if (StorageController.Delete(DeleteData.DeletePath, DeleteData.PermanentDelete, PostDeleteEvent: (se, arg) =>
                                                                {
                                                                    if (!DeleteData.PermanentDelete)
                                                                    {
                                                                        OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Delete");
                                                                    }
                                                                }))
                                                                {
                                                                    if (DeleteData.DeletePath.All((Item) => !Directory.Exists(Item) && !File.Exists(Item)))
                                                                    {
                                                                        Writer.WriteLine("Success");
                                                                        Writer.WriteLine(JsonSerializer.Serialize(OperationRecordList));
                                                                    }
                                                                    else
                                                                    {
                                                                        Writer.WriteLine("Error_Capture");
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    Writer.WriteLine("Error_Failure");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                Writer.WriteLine("Error_NoPermission");
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Writer.WriteLine("Error_NotFound");
                                                    }

                                                    break;
                                                }
                                            case ElevationRenameData RenameData:
                                                {
                                                    if (File.Exists(RenameData.Path) || Directory.Exists(RenameData.Path))
                                                    {
                                                        if (StorageController.CheckCaptured(RenameData.Path))
                                                        {
                                                            Writer.WriteLine("Error_Capture");
                                                        }
                                                        else
                                                        {
                                                            if (StorageController.CheckPermission(FileSystemRights.Modify, Path.GetDirectoryName(RenameData.Path) ?? RenameData.Path))
                                                            {
                                                                string NewName = string.Empty;

                                                                if (StorageController.Rename(RenameData.Path, RenameData.DesireName, (s, e) =>
                                                                {
                                                                    NewName = e.Name;
                                                                }))
                                                                {
                                                                    Writer.WriteLine("Success");
                                                                    Writer.WriteLine(NewName);
                                                                }
                                                                else
                                                                {
                                                                    Writer.WriteLine("Error_Failure");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                Writer.WriteLine("Error_NoPermission");
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Writer.WriteLine("Error_NotFound");
                                                    }

                                                    break;
                                                }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogTracer.Log(ex, $"FullTrustProcess(Elevated) threw an exception, message: {ex.Message}");
                                    File.Delete(TempFilePath);
                                }
                                finally
                                {
                                    File.Delete(DataPath);
                                }
                            }
                        }
                        else
                        {
                            throw new InvalidDataException("Init file is missing");
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("Startup parameter is not correct");
                    }
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"FullTrustProcess threw an exception, message: {ex.Message}");
            }
            finally
            {
                Connection?.Dispose();
                ExitLocker?.Dispose();
                AliveCheckTimer?.Dispose();
                PipeCommandWriteController?.Dispose();
                PipeCommandReadController?.Dispose();
                PipeProgressWriterController?.Dispose();
                LogTracer.MakeSureLogIsFlushed(2000);
            }
        }

        private static async void PipeReadController_OnDataReceived(object sender, NamedPipeDataReceivedArgs e)
        {
            EventDeferral Deferral = e.GetDeferral();

            try
            {
                IDictionary<string, string> Request = JsonSerializer.Deserialize<IDictionary<string, string>>(e.Data);
                IDictionary<string, string> Response = await HandleCommand(Request);
                PipeCommandWriteController?.SendData(JsonSerializer.Serialize(Response));
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, "An exception was threw in responding pipe message");
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception Ex)
            {
                LogTracer.Log(Ex, "UnhandledException");
                LogTracer.MakeSureLogIsFlushed(2000);
            }
        }

        private static void Connection_ServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            LogTracer.Log($"Connection closed, Status: {Enum.GetName(typeof(AppServiceClosedStatus), args.Status)}");

            if (!((PipeCommandWriteController?.IsConnected).GetValueOrDefault()
                   && (PipeCommandReadController?.IsConnected).GetValueOrDefault()
                   && (PipeProgressWriterController?.IsConnected).GetValueOrDefault()))
            {
                ExitLocker.Set();
            }
        }

        private async static void Connection_RequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            AppServiceDeferral Deferral = args.GetDeferral();

            try
            {
                Dictionary<string, string> Command = new Dictionary<string, string>();

                foreach (KeyValuePair<string, object> Pair in args.Request.Message)
                {
                    Command.Add(Pair.Key, Convert.ToString(Pair.Value));
                }

                IDictionary<string, string> Response = await HandleCommand(Command);

                ValueSet Value = new ValueSet();

                foreach (KeyValuePair<string, string> Pair in Response)
                {
                    Value.Add(Pair.Key, Pair.Value);
                }

                await args.Request.SendResponseAsync(Value);
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"An exception was threw in {nameof(Connection_RequestReceived)}");
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async static Task<IDictionary<string, string>> HandleCommand(IDictionary<string, string> CommandValue)
        {
            IDictionary<string, string> Value = new Dictionary<string, string>();

            try
            {
                switch (Enum.Parse(typeof(CommandType), Convert.ToString(CommandValue["CommandType"])))
                {
                    case CommandType.GetUrlTargetPath:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            if (File.Exists(ExecutePath))
                            {
                                string NewPath = ExecutePath;

                                if (!Path.GetExtension(NewPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
                                {
                                    NewPath = Path.Combine(Path.GetDirectoryName(ExecutePath), $"{Path.GetFileNameWithoutExtension(ExecutePath)}.url");
                                    File.Move(ExecutePath, NewPath);
                                }

                                using (ShellItem Item = new ShellItem(NewPath))
                                {
                                    Value.Add("Success", Item.Properties.GetPropertyString(Ole32.PROPERTYKEY.System.Link.TargetUrl));
                                }

                                File.Move(NewPath, ExecutePath);
                            }
                            else
                            {
                                Value.Add("Error", "File not found");
                            }

                            break;
                        }
                    case CommandType.GetThumbnail:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            if (File.Exists(ExecutePath) || Directory.Exists(ExecutePath))
                            {
                                using (ShellItem Item = new ShellItem(ExecutePath))
                                using (Image Thumbnail = Item.GetImage(new Size(128, 128), ShellItemGetImageOptions.BiggerSizeOk))
                                using (Bitmap OriginBitmap = new Bitmap(Thumbnail))
                                using (MemoryStream Stream = new MemoryStream())
                                {
                                    OriginBitmap.MakeTransparent();
                                    OriginBitmap.Save(Stream, ImageFormat.Png);

                                    Value.Add("Success", JsonSerializer.Serialize(Stream.ToArray()));
                                }
                            }
                            else
                            {
                                Value.Add("Error", "File or directory not found");
                            }

                            break;
                        }
                    case CommandType.LaunchUWP:
                        {
                            string[] PathArray = JsonSerializer.Deserialize<string[]>(Convert.ToString(CommandValue["LaunchPathArray"]));

                            if (CommandValue.TryGetValue("PackageFamilyName", out string PackageFamilyName))
                            {
                                if (string.IsNullOrEmpty(PackageFamilyName))
                                {
                                    Value.Add("Error", "PackageFamilyName could not empty");
                                }
                                else
                                {
                                    if (await Helper.LaunchApplicationFromPackageFamilyNameAsync(PackageFamilyName, PathArray))
                                    {
                                        Value.Add("Success", string.Empty);
                                    }
                                    else
                                    {
                                        Value.Add("Error", "Could not launch the UWP");
                                    }
                                }
                            }
                            else if (CommandValue.TryGetValue("AppUserModelId", out string AppUserModelId))
                            {
                                if (string.IsNullOrEmpty(AppUserModelId))
                                {
                                    Value.Add("Error", "AppUserModelId could not empty");
                                }
                                else
                                {
                                    if (await Helper.LaunchApplicationFromAUMIDAsync(AppUserModelId, PathArray))
                                    {
                                        Value.Add("Success", string.Empty);
                                    }
                                    else
                                    {
                                        Value.Add("Error", "Could not launch the UWP");
                                    }
                                }
                            }

                            break;
                        }
                    case CommandType.GetDocumentProperties:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            if (File.Exists(ExecutePath))
                            {
                                Dictionary<string, string> PropertiesDic = new Dictionary<string, string>(9);

                                using (ShellItem Item = new ShellItem(ExecutePath))
                                {
                                    if (Item.IShellItem is Shell32.IShellItem2 IShell2)
                                    {
                                        try
                                        {
                                            string LastAuthor = IShell2.GetString(Ole32.PROPERTYKEY.System.Document.LastAuthor);

                                            if (string.IsNullOrEmpty(LastAuthor))
                                            {
                                                PropertiesDic.Add("LastAuthor", string.Empty);
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("LastAuthor", LastAuthor);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("LastAuthor", string.Empty);
                                        }

                                        try
                                        {
                                            string Version = IShell2.GetString(Ole32.PROPERTYKEY.System.Document.Version);

                                            if (string.IsNullOrEmpty(Version))
                                            {
                                                PropertiesDic.Add("Version", string.Empty);
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("Version", Version);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("Version", string.Empty);
                                        }

                                        try
                                        {
                                            string RevisionNumber = IShell2.GetString(Ole32.PROPERTYKEY.System.Document.RevisionNumber);

                                            if (string.IsNullOrEmpty(RevisionNumber))
                                            {
                                                PropertiesDic.Add("RevisionNumber", string.Empty);
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("RevisionNumber", RevisionNumber);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("RevisionNumber", string.Empty);
                                        }

                                        try
                                        {
                                            string Template = IShell2.GetString(Ole32.PROPERTYKEY.System.Document.Template);

                                            if (string.IsNullOrEmpty(Template))
                                            {
                                                PropertiesDic.Add("Template", string.Empty);
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("Template", Template);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("Template", string.Empty);
                                        }

                                        try
                                        {
                                            int PageCount = IShell2.GetInt32(Ole32.PROPERTYKEY.System.Document.PageCount);

                                            if (PageCount > 0)
                                            {
                                                PropertiesDic.Add("PageCount", Convert.ToString(PageCount));
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("PageCount", string.Empty);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("PageCount", string.Empty);
                                        }

                                        try
                                        {
                                            int WordCount = IShell2.GetInt32(Ole32.PROPERTYKEY.System.Document.WordCount);

                                            if (WordCount > 0)
                                            {
                                                PropertiesDic.Add("WordCount", Convert.ToString(WordCount));
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("WordCount", string.Empty);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("WordCount", string.Empty);
                                        }

                                        try
                                        {
                                            int CharacterCount = IShell2.GetInt32(Ole32.PROPERTYKEY.System.Document.CharacterCount);

                                            if (CharacterCount > 0)
                                            {
                                                PropertiesDic.Add("CharacterCount", Convert.ToString(CharacterCount));
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("CharacterCount", string.Empty);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("CharacterCount", string.Empty);
                                        }

                                        try
                                        {
                                            int LineCount = IShell2.GetInt32(Ole32.PROPERTYKEY.System.Document.LineCount);

                                            if (LineCount > 0)
                                            {
                                                PropertiesDic.Add("LineCount", Convert.ToString(LineCount));
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("LineCount", string.Empty);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("LineCount", string.Empty);
                                        }

                                        try
                                        {
                                            ulong TotalEditingTime = IShell2.GetUInt64(Ole32.PROPERTYKEY.System.Document.TotalEditingTime);

                                            if (TotalEditingTime > 0)
                                            {
                                                PropertiesDic.Add("TotalEditingTime", Convert.ToString(TotalEditingTime));
                                            }
                                            else
                                            {
                                                PropertiesDic.Add("TotalEditingTime", string.Empty);
                                            }
                                        }
                                        catch
                                        {
                                            PropertiesDic.Add("TotalEditingTime", string.Empty);
                                        }
                                    }
                                }

                                Value.Add("Success", JsonSerializer.Serialize(PropertiesDic));
                            }
                            else
                            {
                                Value.Add("Error", "File not found");
                            }

                            break;
                        }
                    case CommandType.GetMIMEContentType:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            Value.Add("Success", Helper.GetMIMEFromPath(ExecutePath));

                            break;
                        }
                    case CommandType.GetHiddenItemData:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            using (ShellItem Item = new ShellItem(ExecutePath))
                            using (Image Thumbnail = Item.GetImage(new Size(128, 128), ShellItemGetImageOptions.BiggerSizeOk))
                            using (Bitmap OriginBitmap = new Bitmap(Thumbnail))
                            using (MemoryStream Stream = new MemoryStream())
                            {
                                OriginBitmap.MakeTransparent();
                                OriginBitmap.Save(Stream, ImageFormat.Png);

                                Value.Add("Success", JsonSerializer.Serialize(new HiddenDataPackage(Item.FileInfo.TypeName, Stream.ToArray())));
                            }

                            break;
                        }
                    case CommandType.GetTooltipText:
                        {
                            string Path = Convert.ToString(CommandValue["Path"]);

                            if (File.Exists(Path) || Directory.Exists(Path))
                            {
                                using (ShellItem Item = new ShellItem(Path))
                                {
                                    Value.Add("Success", Item.GetToolTip(ShellItemToolTipOptions.AllowDelay));
                                }
                            }
                            else
                            {
                                Value.Add("Error", "Path is not exists");
                            }

                            break;
                        }
                    case CommandType.CheckIfEverythingAvailable:
                        {
                            if (EverythingConnector.IsAvailable)
                            {
                                Value.Add("Success", string.Empty);
                            }
                            else
                            {
                                Value.Add("Error", $"Everything is not available, ErrorCode: {Enum.GetName(typeof(EverythingConnector.StateCode), EverythingConnector.GetLastErrorCode())}");
                            }

                            break;
                        }
                    case CommandType.SearchByEverything:
                        {
                            string BaseLocation = Convert.ToString(CommandValue["BaseLocation"]);
                            string SearchWord = Convert.ToString(CommandValue["SearchWord"]);
                            bool SearchAsRegex = Convert.ToBoolean(CommandValue["SearchAsRegex"]);
                            bool IgnoreCase = Convert.ToBoolean(CommandValue["IgnoreCase"]);

                            if (EverythingConnector.IsAvailable)
                            {
                                IEnumerable<string> SearchResult = EverythingConnector.Search(BaseLocation, SearchWord, SearchAsRegex, IgnoreCase);

                                if (SearchResult.Any())
                                {
                                    Value.Add("Success", JsonSerializer.Serialize(SearchResult));
                                }
                                else
                                {
                                    EverythingConnector.StateCode Code = EverythingConnector.GetLastErrorCode();

                                    if (Code == EverythingConnector.StateCode.OK)
                                    {
                                        Value.Add("Success", JsonSerializer.Serialize(SearchResult));
                                    }
                                    else
                                    {
                                        Value.Add("Error", $"Everything report an error, code: {Enum.GetName(typeof(EverythingConnector.StateCode), Code)}");
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error", "Everything is not available");
                            }

                            break;
                        }
                    case CommandType.GetContextMenuItems:
                        {
                            string[] ExecutePath = JsonSerializer.Deserialize<string[]>(Convert.ToString(CommandValue["ExecutePath"]));

                            await Helper.ExecuteOnSTAThreadAsync(() =>
                            {
                                Value.Add("Success", JsonSerializer.Serialize(ContextMenu.GetContextMenuItems(ExecutePath, Convert.ToBoolean(CommandValue["IncludeExtensionItem"]))));
                            });

                            break;
                        }
                    case CommandType.InvokeContextMenuItem:
                        {
                            ContextMenuPackage Package = JsonSerializer.Deserialize<ContextMenuPackage>(Convert.ToString(CommandValue["DataPackage"]));

                            await Helper.ExecuteOnSTAThreadAsync(() =>
                            {
                                if (ContextMenu.InvokeVerb(Package))
                                {
                                    Value.Add("Success", string.Empty);
                                }
                                else
                                {
                                    Value.Add("Error", $"Execute Id: \"{Package.Id}\", Verb: \"{Package.Verb}\" failed");
                                }
                            });

                            break;
                        }
                    case CommandType.CreateLink:
                        {
                            LinkDataPackage Package = JsonSerializer.Deserialize<LinkDataPackage>(Convert.ToString(CommandValue["DataPackage"]));

                            string Argument = string.Join(" ", Package.Argument.Select((Para) => (Para.Contains(" ") && !Para.StartsWith("\"") && !Para.EndsWith("\"")) ? $"\"{Para}\"" : Para).ToArray());

                            using (ShellLink Link = ShellLink.Create(StorageController.GenerateUniquePath(Package.LinkPath), Package.LinkTargetPath, Package.Comment, Package.WorkDirectory, Argument))
                            {
                                Link.ShowState = (FormWindowState)Package.WindowState;
                                Link.RunAsAdministrator = Package.NeedRunAsAdmin;

                                if (Package.HotKey > 0)
                                {
                                    Link.HotKey = (((Package.HotKey >= 112 && Package.HotKey <= 135) || (Package.HotKey >= 96 && Package.HotKey <= 105)) || (Package.HotKey >= 96 && Package.HotKey <= 105)) ? (Keys)Package.HotKey : (Keys)Package.HotKey | Keys.Control | Keys.Alt;
                                }
                            }

                            Value.Add("Success", string.Empty);

                            break;
                        }
                    case CommandType.GetVariablePath:
                        {
                            string Variable = Convert.ToString(CommandValue["Variable"]);

                            string Env = Environment.GetEnvironmentVariable(Variable);

                            if (string.IsNullOrEmpty(Env))
                            {
                                Value.Add("Error", "Could not found EnvironmentVariable");
                            }
                            else
                            {
                                Value.Add("Success", Env);
                            }

                            break;
                        }
                    case CommandType.GetVariablePathSuggestion:
                        string PartialVariable = Convert.ToString(CommandValue["PartialVariable"]);

                        if (PartialVariable.Count(x => x == '%') == 1)
                        {
                            PartialVariable = PartialVariable.Replace("%", "");
                            var EnvironmentVariableList = LoadEnvironmentStringPaths();
                            var VariableSuggestionList = EnvironmentVariableList.Where(x => x.StartsWith(PartialVariable, StringComparison.OrdinalIgnoreCase));
                            Value.Add("Success", JsonSerializer.Serialize(VariableSuggestionList));
                        }
                        else
                        {
                            Value.Add("Failure", "Unexpected Partial Environmental String");
                        }
                        

                        break;
                    case CommandType.CreateNew:
                        {
                            string CreateNewPath = Convert.ToString(CommandValue["NewPath"]);
                            string UniquePath = StorageController.GenerateUniquePath(CreateNewPath);

                            CreateType Type = (CreateType)Enum.Parse(typeof(CreateType), Convert.ToString(CommandValue["Type"]));

                            if (StorageController.CheckPermission(Type == CreateType.File ? FileSystemRights.CreateFiles : FileSystemRights.CreateDirectories, Path.GetDirectoryName(UniquePath) ?? UniquePath))
                            {
                                if (StorageController.Create(Type, UniquePath))
                                {
                                    Value.Add("Success", string.Empty);
                                }
                                else if (Marshal.GetLastWin32Error() == 5)
                                {
                                    LaunchCurrentAsElevated();
                                }
                                else
                                {
                                    Value.Add("Error_Failure", "Error happened when create a new file or directory");
                                }
                            }
                            else
                            {
                                LaunchCurrentAsElevated();
                            }

                            void LaunchCurrentAsElevated()
                            {
                                using (Process AdminProcess = CreateNewProcessAsElevated(new ElevationCreateNewData(Type, UniquePath)))
                                using (Process CurrentProcess = Process.GetCurrentProcess())
                                {
                                    AdminProcess.WaitForExit();

                                    string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{AdminProcess.Id}");

                                    if (File.Exists(TempFilePath))
                                    {
                                        try
                                        {
                                            string OriginData = File.ReadAllText(TempFilePath, Encoding.UTF8).Replace(Environment.NewLine, string.Empty);

                                            switch (OriginData)
                                            {
                                                case "Success":
                                                    {
                                                        Value.Add("Success", UniquePath);
                                                        break;
                                                    }
                                                case "Error_NoPermission":
                                                    {
                                                        Value.Add("Error_NoPermission", "Do not have enough permission");
                                                        break;
                                                    }
                                                case "Error_Failure":
                                                    {
                                                        Value.Add("Error_Failure", "Error happened when create new");
                                                        break;
                                                    }
                                            }
                                        }
                                        finally
                                        {
                                            File.Delete(TempFilePath);
                                        }
                                    }
                                    else
                                    {
                                        Value.Add("Error", "Could not found template file");
                                    }
                                }
                            }

                            break;
                        }
                    case CommandType.Rename:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);
                            string DesireName = Convert.ToString(CommandValue["DesireName"]);

                            if (File.Exists(ExecutePath) || Directory.Exists(ExecutePath))
                            {
                                if (StorageController.CheckCaptured(ExecutePath))
                                {
                                    Value.Add("Error_Capture", "An error occurred while renaming the files");
                                }
                                else
                                {
                                    if (StorageController.CheckPermission(FileSystemRights.Modify, Path.GetDirectoryName(ExecutePath) ?? ExecutePath))
                                    {
                                        string NewName = string.Empty;

                                        if (StorageController.Rename(ExecutePath, DesireName, (s, e) =>
                                        {
                                            NewName = e.Name;
                                        }))
                                        {
                                            Value.Add("Success", NewName);
                                        }
                                        else if (Marshal.GetLastWin32Error() == 5)
                                        {
                                            LaunchCurrentAsElevated();
                                        }
                                        else
                                        {
                                            Value.Add("Error_Failure", "Error happened when rename");
                                        }
                                    }
                                    else
                                    {
                                        LaunchCurrentAsElevated();
                                    }

                                    void LaunchCurrentAsElevated()
                                    {
                                        using (Process AdminProcess = CreateNewProcessAsElevated(new ElevationRenameData(ExecutePath, DesireName)))
                                        using (Process CurrentProcess = Process.GetCurrentProcess())
                                        {
                                            AdminProcess.WaitForExit();

                                            string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{AdminProcess.Id}");

                                            if (File.Exists(TempFilePath))
                                            {
                                                try
                                                {
                                                    string[] OriginData = File.ReadAllLines(TempFilePath, Encoding.UTF8);

                                                    switch (OriginData[0])
                                                    {
                                                        case "Success":
                                                            {
                                                                Value.Add("Success", OriginData[1]);
                                                                break;
                                                            }
                                                        case "Error_Capture":
                                                            {
                                                                Value.Add("Error_Capture", "An error occurred while renaming the files");
                                                                break;
                                                            }
                                                        case "Error_NoPermission":
                                                            {
                                                                Value.Add("Error_NoPermission", "Do not have enough permission");
                                                                break;
                                                            }
                                                        case "Error_Failure":
                                                            {
                                                                Value.Add("Error_Failure", "Error happened when rename");
                                                                break;
                                                            }
                                                    }
                                                }
                                                finally
                                                {
                                                    File.Delete(TempFilePath);
                                                }
                                            }
                                            else
                                            {
                                                Value.Add("Error", "Could not found template file");
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error_NotFound", "Path is not found");
                            }

                            break;
                        }
                    case CommandType.GetInstalledApplication:
                        {
                            string PFN = Convert.ToString(CommandValue["PackageFamilyName"]);

                            InstalledApplicationPackage Pack = await Helper.GetInstalledApplicationAsync(PFN);

                            if (Pack != null)
                            {
                                Value.Add("Success", JsonSerializer.Serialize(Pack));
                            }
                            else
                            {
                                Value.Add("Error", "Could not found the package with PFN");
                            }

                            break;
                        }
                    case CommandType.GetAllInstalledApplication:
                        {
                            Value.Add("Success", JsonSerializer.Serialize(await Helper.GetInstalledApplicationAsync()));

                            break;
                        }
                    case CommandType.CheckPackageFamilyNameExist:
                        {
                            string PFN = Convert.ToString(CommandValue["PackageFamilyName"]);

                            Value.Add("Success", Convert.ToString(Helper.CheckIfPackageFamilyNameExist(PFN)));

                            break;
                        }
                    case CommandType.UpdateUrl:
                        {
                            UrlDataPackage Package = JsonSerializer.Deserialize<UrlDataPackage>(Convert.ToString(CommandValue["DataPackage"]));

                            if (File.Exists(Package.UrlPath))
                            {
                                List<string> SplitList;

                                using (StreamReader Reader = new StreamReader(Package.UrlPath, true))
                                {
                                    SplitList = Reader.ReadToEnd().Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).ToList();

                                    string UrlLine = SplitList.FirstOrDefault((Line) => Line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));

                                    if (!string.IsNullOrEmpty(UrlLine))
                                    {
                                        SplitList.Remove(UrlLine);
                                        SplitList.Add($"URL={Package.UrlTargetPath}");
                                    }
                                }

                                using (StreamWriter Writer = new StreamWriter(Package.UrlPath, false))
                                {
                                    Writer.Write(string.Join(Environment.NewLine, SplitList));
                                }
                            }
                            else
                            {
                                Value.Add("Error", "File not found");
                            }

                            break;
                        }
                    case CommandType.UpdateLink:
                        {
                            LinkDataPackage Package = JsonSerializer.Deserialize<LinkDataPackage>(Convert.ToString(CommandValue["DataPackage"]));

                            string Argument = string.Join(" ", Package.Argument.Select((Para) => (Para.Contains(" ") && !Para.StartsWith("\"") && !Para.EndsWith("\"")) ? $"\"{Para}\"" : Para).ToArray());

                            if (File.Exists(Package.LinkPath))
                            {
                                if (Path.IsPathRooted(Package.LinkTargetPath))
                                {
                                    using (ShellLink Link = new ShellLink(Package.LinkPath))
                                    {
                                        Link.TargetPath = Package.LinkTargetPath;
                                        Link.WorkingDirectory = Package.WorkDirectory;
                                        Link.ShowState = (FormWindowState)Package.WindowState;
                                        Link.RunAsAdministrator = Package.NeedRunAsAdmin;
                                        Link.Description = Package.Comment;

                                        if (Package.HotKey > 0)
                                        {
                                            Link.HotKey = (((Package.HotKey >= 112 && Package.HotKey <= 135) || (Package.HotKey >= 96 && Package.HotKey <= 105)) || (Package.HotKey >= 96 && Package.HotKey <= 105)) ? (Keys)Package.HotKey : (Keys)Package.HotKey | Keys.Control | Keys.Alt;
                                        }
                                        else
                                        {
                                            Link.HotKey = Keys.None;
                                        }
                                    }
                                }
                                else if (Helper.CheckIfPackageFamilyNameExist(Package.LinkTargetPath))
                                {
                                    using (ShellLink Link = new ShellLink(Package.LinkPath))
                                    {
                                        Link.ShowState = (FormWindowState)Package.WindowState;
                                        Link.Description = Package.Comment;

                                        if (Package.HotKey > 0)
                                        {
                                            Link.HotKey = ((Package.HotKey >= 112 && Package.HotKey <= 135) || (Package.HotKey >= 96 && Package.HotKey <= 105)) ? (Keys)Package.HotKey : (Keys)Package.HotKey | Keys.Control | Keys.Alt;
                                        }
                                        else
                                        {
                                            Link.HotKey = Keys.None;
                                        }
                                    }
                                }

                                Value.Add("Success", string.Empty);
                            }
                            else
                            {
                                Value.Add("Error", "Path is not found");
                            }

                            break;
                        }
                    case CommandType.SetFileAttribute:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);
                            KeyValuePair<ModifyAttributeAction, System.IO.FileAttributes>[] AttributeGourp = JsonSerializer.Deserialize<KeyValuePair<ModifyAttributeAction, System.IO.FileAttributes>[]>(Convert.ToString(CommandValue["Attributes"]));

                            if (File.Exists(ExecutePath))
                            {
                                FileInfo File = new FileInfo(ExecutePath);

                                foreach (KeyValuePair<ModifyAttributeAction, System.IO.FileAttributes> AttributePair in AttributeGourp)
                                {
                                    if (AttributePair.Key == ModifyAttributeAction.Add)
                                    {
                                        File.Attributes |= AttributePair.Value;
                                    }
                                    else
                                    {
                                        File.Attributes &= ~AttributePair.Value;
                                    }
                                }

                                Value.Add("Success", string.Empty);
                            }
                            else if (Directory.Exists(ExecutePath))
                            {
                                DirectoryInfo Dir = new DirectoryInfo(ExecutePath);

                                foreach (KeyValuePair<ModifyAttributeAction, System.IO.FileAttributes> AttributePair in AttributeGourp)
                                {
                                    if (AttributePair.Key == ModifyAttributeAction.Add)
                                    {
                                        if (AttributePair.Value == System.IO.FileAttributes.ReadOnly)
                                        {
                                            foreach (FileInfo SubFile in Helper.GetAllSubFiles(Dir))
                                            {
                                                SubFile.Attributes |= AttributePair.Value;
                                            }
                                        }
                                        else
                                        {
                                            Dir.Attributes |= AttributePair.Value;
                                        }
                                    }
                                    else
                                    {
                                        if (AttributePair.Value == System.IO.FileAttributes.ReadOnly)
                                        {
                                            foreach (FileInfo SubFile in Helper.GetAllSubFiles(Dir))
                                            {
                                                SubFile.Attributes &= ~AttributePair.Value;
                                            }
                                        }
                                        else
                                        {
                                            Dir.Attributes &= ~AttributePair.Value;
                                        }
                                    }
                                }

                                Value.Add("Success", string.Empty);
                            }
                            else
                            {
                                Value.Add("Error", "Path not found");
                            }

                            break;
                        }
                    case CommandType.GetUrlData:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            if (File.Exists(ExecutePath))
                            {
                                using (ShellItem Item = new ShellItem(ExecutePath))
                                {
                                    string UrlPath = Item.Properties.GetPropertyString(Ole32.PROPERTYKEY.System.Link.TargetUrl);

                                    using (Image IconImage = Item.GetImage(new Size(150, 150), ShellItemGetImageOptions.BiggerSizeOk | ShellItemGetImageOptions.ResizeToFit))
                                    using (MemoryStream IconStream = new MemoryStream())
                                    using (Bitmap TempBitmap = new Bitmap(IconImage))
                                    {
                                        TempBitmap.MakeTransparent();
                                        TempBitmap.Save(IconStream, ImageFormat.Png);

                                        Value.Add("Success", JsonSerializer.Serialize(new UrlDataPackage
                                        {
                                            UrlPath = ExecutePath,
                                            UrlTargetPath = UrlPath,
                                            IconData = IconStream.ToArray()
                                        }));
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error", "File not found");
                            }

                            break;
                        }
                    case CommandType.GetLinkData:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            if (File.Exists(ExecutePath))
                            {
                                StringBuilder ProductCode = new StringBuilder(39);
                                StringBuilder ComponentCode = new StringBuilder(39);

                                if (Msi.MsiGetShortcutTarget(ExecutePath, ProductCode, szComponentCode: ComponentCode).Succeeded)
                                {
                                    uint Length = 0;

                                    StringBuilder ActualPathBuilder = new StringBuilder();

                                    Msi.INSTALLSTATE State = Msi.MsiGetComponentPath(ProductCode.ToString(), ComponentCode.ToString(), ActualPathBuilder, ref Length);

                                    if (State == Msi.INSTALLSTATE.INSTALLSTATE_LOCAL || State == Msi.INSTALLSTATE.INSTALLSTATE_SOURCE)
                                    {
                                        string ActualPath = ActualPathBuilder.ToString();

                                        foreach (Match Var in Regex.Matches(ActualPath, @"(?<=(%))[\s\S]+(?=(%))"))
                                        {
                                            ActualPath = ActualPath.Replace($"%{Var.Value}%", Environment.GetEnvironmentVariable(Var.Value));
                                        }

                                        using (ShellItem Item = new ShellItem(ActualPath))
                                        using (Image IconImage = Item.GetImage(new Size(150, 150), ShellItemGetImageOptions.BiggerSizeOk | ShellItemGetImageOptions.ResizeToFit))
                                        using (MemoryStream IconStream = new MemoryStream())
                                        using (Bitmap TempBitmap = new Bitmap(IconImage))
                                        {
                                            TempBitmap.MakeTransparent();
                                            TempBitmap.Save(IconStream, ImageFormat.Png);

                                            Value.Add("Success", JsonSerializer.Serialize(new LinkDataPackage
                                            {
                                                LinkPath = ExecutePath,
                                                LinkTargetPath = ActualPath,
                                                IconData = IconStream.ToArray()
                                            }));
                                        }
                                    }
                                    else
                                    {
                                        Value.Add("Error", "Lnk file could not be analysis by MsiGetShortcutTarget");
                                    }
                                }
                                else
                                {
                                    using (ShellLink Link = new ShellLink(ExecutePath))
                                    {
                                        if (string.IsNullOrEmpty(Link.TargetPath))
                                        {
                                            string PackageFamilyName = Helper.GetPackageFamilyNameFromUWPShellLink(ExecutePath);

                                            if (string.IsNullOrEmpty(PackageFamilyName))
                                            {
                                                Value.Add("Error", "TargetPath is invalid");
                                            }
                                            else
                                            {
                                                byte[] IconData = await Helper.GetIconDataFromPackageFamilyNameAsync(PackageFamilyName);

                                                Value.Add("Success", JsonSerializer.Serialize(new LinkDataPackage
                                                {
                                                    LinkPath = ExecutePath,
                                                    LinkTargetPath = PackageFamilyName,
                                                    WindowState = (WindowState)Enum.Parse(typeof(WindowState), Enum.GetName(typeof(FormWindowState), Link.ShowState)),
                                                    HotKey = (int)Link.HotKey,
                                                    Comment = Link.Description,
                                                    IconData = IconData
                                                }));
                                            }
                                        }
                                        else
                                        {
                                            MatchCollection Collection = Regex.Matches(Link.Arguments, "[^ \"]+|\"[^\"]*\"");

                                            List<string> Arguments = new List<string>(Collection.Count);

                                            foreach (Match Mat in Collection)
                                            {
                                                Arguments.Add(Mat.Value);
                                            }

                                            string ActualPath = Link.TargetPath;

                                            foreach (Match Var in Regex.Matches(ActualPath, @"(?<=(%))[\s\S]+(?=(%))"))
                                            {
                                                ActualPath = ActualPath.Replace($"%{Var.Value}%", Environment.GetEnvironmentVariable(Var.Value));
                                            }

                                            using (Image IconImage = Link.GetImage(new Size(120, 120), ShellItemGetImageOptions.BiggerSizeOk | ShellItemGetImageOptions.ScaleUp))
                                            using (MemoryStream IconStream = new MemoryStream())
                                            using (Bitmap TempBitmap = new Bitmap(IconImage))
                                            {
                                                TempBitmap.MakeTransparent();
                                                TempBitmap.Save(IconStream, ImageFormat.Png);

                                                Value.Add("Success", JsonSerializer.Serialize(new LinkDataPackage
                                                {
                                                    LinkPath = ExecutePath,
                                                    LinkTargetPath = ActualPath,
                                                    WorkDirectory = Link.WorkingDirectory,
                                                    WindowState = (WindowState)Enum.Parse(typeof(WindowState), Enum.GetName(typeof(FormWindowState), Link.ShowState)),
                                                    HotKey = (int)Link.HotKey,
                                                    Comment = Link.Description,
                                                    NeedRunAsAdmin = Link.RunAsAdministrator,
                                                    IconData = IconStream.ToArray(),
                                                    Argument = Arguments.ToArray()
                                                }));
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error", "File is not exist");
                            }

                            break;
                        }
                    case CommandType.InterceptWinE:
                        {
                            string AliasLocation = null;

                            try
                            {
                                using (Process Pro = Process.Start(new ProcessStartInfo
                                {
                                    FileName = "powershell.exe",
                                    Arguments = "-Command \"Get-Command RX-Explorer | Format-List -Property Source\"",
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false
                                }))
                                {
                                    try
                                    {
                                        string OutputString = Pro.StandardOutput.ReadToEnd();

                                        if (!string.IsNullOrWhiteSpace(OutputString))
                                        {
                                            string Path = OutputString.Replace(Environment.NewLine, string.Empty).Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

                                            if (File.Exists(Path))
                                            {
                                                AliasLocation = Path;
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        if (!Pro.WaitForExit(1000))
                                        {
                                            Pro.Kill();
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogTracer.Log(ex, "Could not get alias location by Powershell");
                            }

                            if (string.IsNullOrEmpty(AliasLocation))
                            {
                                string[] EnvironmentVariables = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)
                                                                           .Split(new string[] { ";" }, StringSplitOptions.RemoveEmptyEntries)
                                                                           .Concat(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)
                                                                                              .Split(new string[] { ";" }, StringSplitOptions.RemoveEmptyEntries))
                                                                           .Distinct()
                                                                           .ToArray();

                                if (EnvironmentVariables.Where((Var) => Var.Contains("WindowsApps")).Select((Var) => Path.Combine(Var, "RX-Explorer.exe")).FirstOrDefault((Path) => File.Exists(Path)) is string Location)
                                {
                                    AliasLocation = Location;
                                }
                                else
                                {
                                    string AppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                                    if (!string.IsNullOrEmpty(AppDataPath) && Directory.Exists(AppDataPath))
                                    {
                                        string WindowsAppsPath = Path.Combine(AppDataPath, "Microsoft", "WindowsApps");

                                        if (Directory.Exists(WindowsAppsPath))
                                        {
                                            string RXPath = Path.Combine(WindowsAppsPath, "RX-Explorer.exe");

                                            if (File.Exists(RXPath))
                                            {
                                                AliasLocation = RXPath;
                                            }
                                        }
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(AliasLocation))
                            {
                                StorageFile InterceptFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/Intercept_WIN_E.reg"));
                                StorageFile TempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("Intercept_WIN_E_Temp.reg", CreationCollisionOption.ReplaceExisting);

                                using (Stream FileStream = await InterceptFile.OpenStreamForReadAsync())
                                using (StreamReader Reader = new StreamReader(FileStream))
                                {
                                    string Content = await Reader.ReadToEndAsync();

                                    using (Stream TempStream = await TempFile.OpenStreamForWriteAsync())
                                    using (StreamWriter Writer = new StreamWriter(TempStream, Encoding.Unicode))
                                    {
                                        await Writer.WriteAsync(Content.Replace("<FillActualAliasPathInHere>", $"{AliasLocation.Replace(@"\", @"\\")} %1"));
                                    }
                                }

                                IReadOnlyList<HWND> WindowsBeforeStartup = Helper.GetCurrentWindowsHandle();

                                using (Process RegisterProcess = new Process())
                                {
                                    RegisterProcess.StartInfo.FileName = TempFile.Path;
                                    RegisterProcess.StartInfo.UseShellExecute = true;
                                    RegisterProcess.Start();

                                    SetWindowsZPosition(RegisterProcess, WindowsBeforeStartup);

                                    RegisterProcess.WaitForExit();
                                }

                                try
                                {
                                    using (RegistryKey Key = Registry.ClassesRoot.OpenSubKey("Folder", false)?.OpenSubKey("shell", false)?.OpenSubKey("opennewwindow", false)?.OpenSubKey("command", false))
                                    {
                                        if (Key != null)
                                        {
                                            if (Convert.ToString(Key.GetValue(string.Empty)).Equals($"{AliasLocation} %1", StringComparison.OrdinalIgnoreCase) && Key.GetValue("DelegateExecute") == null)
                                            {
                                                Value.Add("Success", string.Empty);
                                            }
                                            else
                                            {
                                                Value.Add("Error", "Registry verification failed");
                                            }
                                        }
                                        else
                                        {
                                            Value.Add("Success", string.Empty);
                                        }
                                    }
                                }
                                catch
                                {
                                    Value.Add("Success", string.Empty);
                                }
                            }
                            else
                            {
                                Value.Add("Error", "Alias file is not exists");
                            }

                            break;
                        }
                    case CommandType.RestoreWinE:
                        {
                            StorageFile RestoreFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/Restore_WIN_E.reg"));

                            IReadOnlyList<HWND> WindowsBeforeStartup = Helper.GetCurrentWindowsHandle();

                            using (Process UnregisterProcess = new Process())
                            {
                                UnregisterProcess.StartInfo.FileName = RestoreFile.Path;
                                UnregisterProcess.StartInfo.UseShellExecute = true;
                                UnregisterProcess.Start();

                                SetWindowsZPosition(UnregisterProcess, WindowsBeforeStartup);

                                UnregisterProcess.WaitForExit();
                            }

                            try
                            {
                                RegistryKey Key = Registry.ClassesRoot.OpenSubKey("Folder", false)?.OpenSubKey("shell", false)?.OpenSubKey("opennewwindow", false)?.OpenSubKey("command", false);

                                if (Key != null)
                                {
                                    try
                                    {
                                        if (Convert.ToString(Key.GetValue("DelegateExecute")) == "{11dbb47c-a525-400b-9e80-a54615a090c0}" && string.IsNullOrEmpty(Convert.ToString(Key.GetValue(string.Empty))))
                                        {
                                            Value.Add("Success", string.Empty);
                                        }
                                        else
                                        {
                                            Value.Add("Error", "Registry verification failed");
                                        }
                                    }
                                    finally
                                    {
                                        Key.Dispose();
                                    }
                                }
                                else
                                {
                                    Value.Add("Success", string.Empty);
                                }
                            }
                            catch (Exception ex)
                            {
                                LogTracer.Log(ex, "Could not check the registry");
                                Value.Add("Success", string.Empty);
                            }

                            break;
                        }
                    case CommandType.Identity:
                        {
                            Value.Add("Identity", "FullTrustProcess");

                            break;
                        }
                    case CommandType.Quicklook:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);

                            if (!string.IsNullOrEmpty(ExecutePath))
                            {
                                QuicklookConnector.SendMessage(ExecutePath);
                            }

                            break;
                        }
                    case CommandType.Check_Quicklook:
                        {
                            Value.Add("Check_QuicklookIsAvaliable_Result", Convert.ToString(QuicklookConnector.CheckQuicklookIsAvaliable()));

                            break;
                        }
                    case CommandType.Get_Association:
                        {
                            string Path = Convert.ToString(CommandValue["ExecutePath"]);

                            Value.Add("Associate_Result", JsonSerializer.Serialize(ExtensionAssociate.GetAllAssociation(Path)));

                            break;
                        }
                    case CommandType.Default_Association:
                        {
                            string Path = Convert.ToString(CommandValue["ExecutePath"]);

                            Value.Add("Success", ExtensionAssociate.GetDefaultProgramPathRelated(Path));

                            break;
                        }
                    case CommandType.Get_RecycleBinItems:
                        {
                            string RecycleItemResult = RecycleBinController.GenerateRecycleItemsByJson();

                            if (string.IsNullOrEmpty(RecycleItemResult))
                            {
                                Value.Add("Error", "Could not get recycle items");
                            }
                            else
                            {
                                Value.Add("RecycleBinItems_Json_Result", RecycleItemResult);
                            }

                            break;
                        }
                    case CommandType.EmptyRecycleBin:
                        {
                            Value.Add("RecycleBinItems_Clear_Result", Convert.ToString(RecycleBinController.EmptyRecycleBin()));

                            break;
                        }
                    case CommandType.Restore_RecycleItem:
                        {
                            string[] PathList = JsonSerializer.Deserialize<string[]>(Convert.ToString(CommandValue["ExecutePath"]));

                            Value.Add("Restore_Result", Convert.ToString(RecycleBinController.Restore(PathList)));

                            break;
                        }
                    case CommandType.Delete_RecycleItem:
                        {
                            string Path = Convert.ToString(CommandValue["ExecutePath"]);

                            Value.Add("Delete_Result", Convert.ToString(RecycleBinController.Delete(Path)));

                            break;
                        }
                    case CommandType.EjectUSB:
                        {
                            string Path = Convert.ToString(CommandValue["ExecutePath"]);

                            if (string.IsNullOrEmpty(Path))
                            {
                                Value.Add("EjectResult", Convert.ToString(false));
                            }
                            else
                            {
                                Value.Add("EjectResult", Convert.ToString(USBController.EjectDevice(Path)));
                            }

                            break;
                        }
                    case CommandType.UnlockOccupy:
                        {
                            string Path = Convert.ToString(CommandValue["ExecutePath"]);
                            bool ForceClose = Convert.ToBoolean(CommandValue["ForceClose"]);

                            if (File.Exists(Path))
                            {
                                if (StorageController.CheckCaptured(Path))
                                {
                                    IReadOnlyList<Process> LockingProcesses = StorageController.GetLockingProcesses(Path);

                                    try
                                    {
                                        foreach (Process Pro in LockingProcesses)
                                        {
                                            if (ForceClose || !Pro.CloseMainWindow())
                                            {
                                                Pro.Kill();
                                            }
                                        }

                                        Value.Add("Success", string.Empty);
                                    }
                                    catch (Exception ex)
                                    {
                                        LogTracer.Log(ex, $"Kill process failed, reason: {ex.Message}");
                                        Value.Add("Error_Failure", "Unoccupied failed");
                                    }
                                    finally
                                    {
                                        foreach (Process Pro in LockingProcesses)
                                        {
                                            try
                                            {
                                                if (!ForceClose)
                                                {
                                                    Pro.WaitForExit();
                                                }

                                                Pro.Dispose();
                                            }
                                            catch (Exception ex)
                                            {
                                                LogTracer.Log(ex, "Process is no longer running");
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    Value.Add("Error_NotOccupy", "The file is not occupied");
                                }
                            }
                            else
                            {
                                Value.Add("Error_NotFoundOrNotFile", "Path is not a file");
                            }

                            break;
                        }
                    case CommandType.Copy:
                        {
                            string SourcePathJson = Convert.ToString(CommandValue["SourcePath"]);
                            string DestinationPath = Convert.ToString(CommandValue["DestinationPath"]);

                            CollisionOptions Option = (CollisionOptions)Enum.Parse(typeof(CollisionOptions), Convert.ToString(CommandValue["CollisionOptions"]));

                            List<string> SourcePathList = JsonSerializer.Deserialize<List<string>>(SourcePathJson);
                            List<string> OperationRecordList = new List<string>();

                            if (SourcePathList.All((Item) => Directory.Exists(Item) || File.Exists(Item)))
                            {
                                if (StorageController.CheckPermission(FileSystemRights.Modify, DestinationPath))
                                {
                                    if (StorageController.Copy(SourcePathList, DestinationPath, Option, (s, e) =>
                                    {
                                        PipeProgressWriterController?.SendData(Convert.ToString(e.ProgressPercentage));
                                    },
                                    (se, arg) =>
                                    {
                                        if (arg.Result == HRESULT.S_OK)
                                        {
                                            if (arg.DestItem == null || string.IsNullOrEmpty(arg.Name))
                                            {
                                                OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Copy||{Path.Combine(arg.DestFolder.FileSystemPath, arg.SourceItem.Name)}");
                                            }
                                            else
                                            {
                                                OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Copy||{Path.Combine(arg.DestFolder.FileSystemPath, arg.Name)}");
                                            }
                                        }
                                        else if (arg.Result == HRESULT.COPYENGINE_S_USER_IGNORED)
                                        {
                                            Value.Add("Error_UserCancel", "User stop the operation");
                                        }
                                    }))
                                    {
                                        if (!Value.ContainsKey("Error_UserCancel"))
                                        {
                                            Value.Add("Success", JsonSerializer.Serialize(OperationRecordList));
                                        }
                                    }
                                    else if (Marshal.GetLastWin32Error() == 5)
                                    {
                                        LaunchCurrentAsElevated();
                                    }
                                    else
                                    {
                                        Value.Add("Error_Failure", "An error occurred while copying the files");
                                    }
                                }
                                else
                                {
                                    LaunchCurrentAsElevated();
                                }

                                void LaunchCurrentAsElevated()
                                {
                                    using (Process AdminProcess = CreateNewProcessAsElevated(new ElevationCopyData(SourcePathList, DestinationPath, Option)))
                                    using (Process CurrentProcess = Process.GetCurrentProcess())
                                    {
                                        AdminProcess.WaitForExit();

                                        string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{AdminProcess.Id}");

                                        if (File.Exists(TempFilePath))
                                        {
                                            try
                                            {
                                                string[] OriginData = File.ReadAllLines(TempFilePath, Encoding.UTF8);

                                                switch (OriginData[0])
                                                {
                                                    case "Success":
                                                        {
                                                            Value.Add("Success", OriginData[1]);
                                                            break;
                                                        }
                                                    case "Error_NoPermission":
                                                        {
                                                            Value.Add("Error_Capture", "Do not have enough permission");
                                                            break;
                                                        }
                                                    case "Error_Failure":
                                                        {
                                                            Value.Add("Error_Failure", "Error happened when rename");
                                                            break;
                                                        }
                                                }
                                            }
                                            finally
                                            {
                                                File.Delete(TempFilePath);
                                            }
                                        }
                                        else
                                        {
                                            Value.Add("Error", "Could not found template file");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error_NotFound", "One of path in \"Source\" is not a file or directory");
                            }

                            break;
                        }
                    case CommandType.Move:
                        {
                            string SourcePathJson = Convert.ToString(CommandValue["SourcePath"]);
                            string DestinationPath = Convert.ToString(CommandValue["DestinationPath"]);

                            CollisionOptions Option = (CollisionOptions)Enum.Parse(typeof(CollisionOptions), Convert.ToString(CommandValue["CollisionOptions"]));

                            Dictionary<string, string> SourcePathList = JsonSerializer.Deserialize<Dictionary<string, string>>(SourcePathJson);
                            List<string> OperationRecordList = new List<string>();

                            if (SourcePathList.Keys.All((Item) => Directory.Exists(Item) || File.Exists(Item)))
                            {
                                if (SourcePathList.Keys.Any((Item) => StorageController.CheckCaptured(Item)))
                                {
                                    Value.Add("Error_Capture", "An error occurred while moving the folder");
                                }
                                else
                                {
                                    if (StorageController.CheckPermission(FileSystemRights.Modify, DestinationPath)
                                        && SourcePathList.Keys.All((Path) => StorageController.CheckPermission(FileSystemRights.Modify, System.IO.Path.GetDirectoryName(Path) ?? Path)))
                                    {
                                        if (StorageController.Move(SourcePathList, DestinationPath, Option, (s, e) =>
                                        {
                                            PipeProgressWriterController?.SendData(Convert.ToString(e.ProgressPercentage));
                                        },
                                        (se, arg) =>
                                        {
                                            if (arg.Result == HRESULT.COPYENGINE_S_DONT_PROCESS_CHILDREN)
                                            {
                                                if (arg.DestItem == null || string.IsNullOrEmpty(arg.Name))
                                                {
                                                    OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Move||{Path.Combine(arg.DestFolder.FileSystemPath, arg.SourceItem.Name)}");
                                                }
                                                else
                                                {
                                                    OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Move||{Path.Combine(arg.DestFolder.FileSystemPath, arg.Name)}");
                                                }
                                            }
                                            else if (arg.Result == HRESULT.COPYENGINE_S_USER_IGNORED)
                                            {
                                                Value.Add("Error_UserCancel", "User stop the operation");
                                            }
                                        }))
                                        {
                                            if (!Value.ContainsKey("Error_UserCancel"))
                                            {
                                                if (SourcePathList.Keys.All((Item) => !Directory.Exists(Item) && !File.Exists(Item)))
                                                {
                                                    Value.Add("Success", JsonSerializer.Serialize(OperationRecordList));
                                                }
                                                else
                                                {
                                                    Value.Add("Error_Capture", "An error occurred while moving the files");
                                                }
                                            }
                                        }
                                        else if (Marshal.GetLastWin32Error() == 5)
                                        {
                                            LaunchCurrentAsElevated();
                                        }
                                        else
                                        {
                                            Value.Add("Error_Failure", "An error occurred while moving the files");
                                        }
                                    }
                                    else
                                    {
                                        LaunchCurrentAsElevated();
                                    }

                                    void LaunchCurrentAsElevated()
                                    {
                                        using (Process AdminProcess = CreateNewProcessAsElevated(new ElevationMoveData(SourcePathList, DestinationPath, Option)))
                                        using (Process CurrentProcess = Process.GetCurrentProcess())
                                        {
                                            AdminProcess.WaitForExit();

                                            string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{AdminProcess.Id}");

                                            if (File.Exists(TempFilePath))
                                            {
                                                try
                                                {
                                                    string[] OriginData = File.ReadAllLines(TempFilePath, Encoding.UTF8);

                                                    switch (OriginData[0])
                                                    {
                                                        case "Success":
                                                            {
                                                                Value.Add("Success", OriginData[1]);
                                                                break;
                                                            }
                                                        case "Error_Capture":
                                                            {
                                                                Value.Add("Error_Capture", "An error occurred while renaming the files");
                                                                break;
                                                            }
                                                        case "Error_NoPermission":
                                                            {
                                                                Value.Add("Error_Capture", "Do not have enough permission");
                                                                break;
                                                            }
                                                        case "Error_Failure":
                                                            {
                                                                Value.Add("Error_Failure", "Error happened when rename");
                                                                break;
                                                            }
                                                    }
                                                }
                                                finally
                                                {
                                                    File.Delete(TempFilePath);
                                                }
                                            }
                                            else
                                            {
                                                Value.Add("Error", "Could not found template file");
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error_NotFound", "One of path in \"Source\" is not a file or directory");
                            }

                            break;
                        }
                    case CommandType.Delete:
                        {
                            string ExecutePathJson = Convert.ToString(CommandValue["ExecutePath"]);

                            bool PermanentDelete = Convert.ToBoolean(CommandValue["PermanentDelete"]);

                            List<string> ExecutePathList = JsonSerializer.Deserialize<List<string>>(ExecutePathJson);
                            List<string> OperationRecordList = new List<string>();

                            if (ExecutePathList.All((Item) => Directory.Exists(Item) || File.Exists(Item)))
                            {
                                if (ExecutePathList.Any((Item) => StorageController.CheckCaptured(Item)))
                                {
                                    Value.Add("Error_Capture", "An error occurred while deleting the files");
                                }
                                else
                                {
                                    if (ExecutePathList.All((Path) => StorageController.CheckPermission(FileSystemRights.Modify, System.IO.Path.GetDirectoryName(Path) ?? Path)))
                                    {
                                        if (StorageController.Delete(ExecutePathList, PermanentDelete, (s, e) =>
                                        {
                                            PipeProgressWriterController?.SendData(Convert.ToString(e.ProgressPercentage));
                                        },
                                        (se, arg) =>
                                        {
                                            if (!PermanentDelete)
                                            {
                                                OperationRecordList.Add($"{arg.SourceItem.FileSystemPath}||Delete");
                                            }
                                        }))
                                        {
                                            if (ExecutePathList.All((Item) => !Directory.Exists(Item) && !File.Exists(Item)))
                                            {
                                                Value.Add("Success", JsonSerializer.Serialize(OperationRecordList));
                                            }
                                            else
                                            {
                                                Value.Add("Error_Capture", "An error occurred while deleting the folder");
                                            }
                                        }
                                        else if (Marshal.GetLastWin32Error() == 5)
                                        {
                                            LaunchCurrentAsElevated();
                                        }
                                        else
                                        {
                                            Value.Add("Error_Failure", "The specified file could not be deleted");
                                        }
                                    }
                                    else
                                    {
                                        LaunchCurrentAsElevated();
                                    }

                                    void LaunchCurrentAsElevated()
                                    {
                                        using (Process AdminProcess = CreateNewProcessAsElevated(new ElevationDeleteData(ExecutePathList, PermanentDelete)))
                                        using (Process CurrentProcess = Process.GetCurrentProcess())
                                        {
                                            AdminProcess.WaitForExit();

                                            string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{AdminProcess.Id}");

                                            if (File.Exists(TempFilePath))
                                            {
                                                try
                                                {
                                                    string[] OriginData = File.ReadAllLines(TempFilePath, Encoding.UTF8);

                                                    switch (OriginData[0])
                                                    {
                                                        case "Success":
                                                            {
                                                                Value.Add("Success", OriginData[1]);
                                                                break;
                                                            }
                                                        case "Error_Capture":
                                                            {
                                                                Value.Add("Error_Capture", "An error occurred while renaming the files");
                                                                break;
                                                            }
                                                        case "Error_NoPermission":
                                                            {
                                                                Value.Add("Error_Capture", "Do not have enough permission");
                                                                break;
                                                            }
                                                        case "Error_Failure":
                                                            {
                                                                Value.Add("Error_Failure", "Error happened when rename");
                                                                break;
                                                            }
                                                    }
                                                }
                                                finally
                                                {
                                                    File.Delete(TempFilePath);
                                                }
                                            }
                                            else
                                            {
                                                Value.Add("Error", "Could not found template file");
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Value.Add("Error_NotFound", "ExecutePath is not a file or directory");
                            }

                            break;
                        }
                    case CommandType.RunExecutable:
                        {
                            string ExecutePath = Convert.ToString(CommandValue["ExecutePath"]);
                            string ExecuteParameter = Convert.ToString(CommandValue["ExecuteParameter"]);
                            string ExecuteAuthority = Convert.ToString(CommandValue["ExecuteAuthority"]);
                            string ExecuteWindowStyle = Convert.ToString(CommandValue["ExecuteWindowStyle"]);
                            string ExecuteWorkDirectory = Convert.ToString(CommandValue["ExecuteWorkDirectory"]);

                            bool ExecuteCreateNoWindow = Convert.ToBoolean(CommandValue["ExecuteCreateNoWindow"]);
                            bool ShouldWaitForExit = Convert.ToBoolean(CommandValue["ExecuteShouldWaitForExit"]);

                            if (!string.IsNullOrEmpty(ExecutePath))
                            {
                                if (StorageController.CheckPermission(FileSystemRights.ReadAndExecute, ExecutePath))
                                {
                                    try
                                    {
                                        await Helper.ExecuteOnSTAThreadAsync(() =>
                                        {
                                            ShowWindowCommand WindowCommand;

                                            if (ExecuteCreateNoWindow)
                                            {
                                                WindowCommand = ShowWindowCommand.SW_HIDE;
                                            }
                                            else
                                            {
                                                switch ((ProcessWindowStyle)Enum.Parse(typeof(ProcessWindowStyle), ExecuteWindowStyle))
                                                {
                                                    case ProcessWindowStyle.Hidden:
                                                        {
                                                            WindowCommand = ShowWindowCommand.SW_HIDE;
                                                            break;
                                                        }
                                                    case ProcessWindowStyle.Minimized:
                                                        {
                                                            WindowCommand = ShowWindowCommand.SW_SHOWMINIMIZED;
                                                            break;
                                                        }
                                                    case ProcessWindowStyle.Maximized:
                                                        {
                                                            WindowCommand = ShowWindowCommand.SW_SHOWMAXIMIZED;
                                                            break;
                                                        }
                                                    default:
                                                        {
                                                            WindowCommand = ShowWindowCommand.SW_NORMAL;
                                                            break;
                                                        }
                                                }
                                            }

                                            Shell32.SHELLEXECUTEINFO ExecuteInfo = new Shell32.SHELLEXECUTEINFO
                                            {
                                                hwnd = HWND.NULL,
                                                lpVerb = ExecuteAuthority == "Administrator" ? "runas" : "open",
                                                cbSize = Marshal.SizeOf<Shell32.SHELLEXECUTEINFO>(),
                                                lpFile = ExecutePath,
                                                lpParameters = string.IsNullOrWhiteSpace(ExecuteParameter) ? null : ExecuteParameter,
                                                lpDirectory = string.IsNullOrWhiteSpace(ExecuteWorkDirectory) ? null : ExecuteWorkDirectory,
                                                fMask = Shell32.ShellExecuteMaskFlags.SEE_MASK_FLAG_NO_UI
                                                        | Shell32.ShellExecuteMaskFlags.SEE_MASK_UNICODE
                                                        | Shell32.ShellExecuteMaskFlags.SEE_MASK_DOENVSUBST
                                                        | Shell32.ShellExecuteMaskFlags.SEE_MASK_NOASYNC
                                                        | Shell32.ShellExecuteMaskFlags.SEE_MASK_NOCLOSEPROCESS,
                                                nShellExecuteShow = WindowCommand,
                                            };

                                            if (Shell32.ShellExecuteEx(ref ExecuteInfo))
                                            {
                                                if (ExecuteInfo.hProcess != HPROCESS.NULL)
                                                {
                                                    IntPtr Buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NtDll.PROCESS_BASIC_INFORMATION>());

                                                    try
                                                    {
                                                        NtDll.PROCESS_BASIC_INFORMATION Info = new NtDll.PROCESS_BASIC_INFORMATION();

                                                        Marshal.StructureToPtr(Info, Buffer, false);

                                                        if (NtDll.NtQueryInformationProcess(ExecuteInfo.hProcess, NtDll.PROCESSINFOCLASS.ProcessBasicInformation, Buffer, Convert.ToUInt32(Marshal.SizeOf<NtDll.PROCESS_BASIC_INFORMATION>()), out _).Succeeded)
                                                        {
                                                            NtDll.PROCESS_BASIC_INFORMATION ResultInfo = Marshal.PtrToStructure<NtDll.PROCESS_BASIC_INFORMATION>(Buffer);

                                                            IReadOnlyList<HWND> WindowsBeforeStartup = Helper.GetCurrentWindowsHandle();

                                                            using (Process OpenedProcess = Process.GetProcessById(ResultInfo.UniqueProcessId.ToInt32()))
                                                            {
                                                                SetWindowsZPosition(OpenedProcess, WindowsBeforeStartup);
                                                            }
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        Marshal.FreeHGlobal(Buffer);
                                                        Kernel32.CloseHandle(ExecuteInfo.hProcess);
                                                    }
                                                }
                                            }
                                            else if (Path.GetExtension(ExecutePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                                            {
                                                Kernel32.CREATE_PROCESS CreationFlag = Kernel32.CREATE_PROCESS.NORMAL_PRIORITY_CLASS | Kernel32.CREATE_PROCESS.CREATE_UNICODE_ENVIRONMENT;

                                                if (ExecuteCreateNoWindow)
                                                {
                                                    CreationFlag |= Kernel32.CREATE_PROCESS.CREATE_NO_WINDOW;
                                                }

                                                Kernel32.STARTUPINFO SInfo = new Kernel32.STARTUPINFO
                                                {
                                                    cb = Convert.ToUInt32(Marshal.SizeOf<Kernel32.STARTUPINFO>()),
                                                    ShowWindowCommand = WindowCommand,
                                                    dwFlags = Kernel32.STARTF.STARTF_USESHOWWINDOW,
                                                };

                                                if (Kernel32.CreateProcess(lpCommandLine: new StringBuilder($"\"{ExecutePath}\" {(string.IsNullOrWhiteSpace(ExecuteParameter) ? string.Empty : ExecuteParameter)}"),
                                                                           bInheritHandles: false,
                                                                           dwCreationFlags: CreationFlag,
                                                                           lpCurrentDirectory: string.IsNullOrWhiteSpace(ExecuteWorkDirectory) ? null : ExecuteWorkDirectory,
                                                                           lpStartupInfo: SInfo,
                                                                           lpProcessInformation: out Kernel32.SafePROCESS_INFORMATION PInfo))

                                                {
                                                    try
                                                    {
                                                        IReadOnlyList<HWND> WindowsBeforeStartup = Helper.GetCurrentWindowsHandle();

                                                        using (Process OpenedProcess = Process.GetProcessById(Convert.ToInt32(PInfo.dwProcessId)))
                                                        {
                                                            SetWindowsZPosition(OpenedProcess, WindowsBeforeStartup);
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        if (!PInfo.hProcess.IsInvalid && !PInfo.hProcess.IsNull)
                                                        {
                                                            PInfo.hProcess.Dispose();
                                                        }

                                                        if (!PInfo.hThread.IsInvalid && !PInfo.hThread.IsNull)
                                                        {
                                                            PInfo.hThread.Dispose();
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                                                }
                                            }
                                            else
                                            {
                                                throw new Win32Exception(Marshal.GetLastWin32Error());
                                            }
                                        });

                                        Value.Add("Success", string.Empty);
                                    }
                                    catch (Exception ex)
                                    {
                                        Value.Add("Error", $"Path: {ExecutePath}, Parameter: {ExecuteParameter}, Authority: {ExecuteAuthority}, ErrorMessage: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    Value.Add("Error_NoPermission", "Do not have enough permission");
                                }
                            }
                            else
                            {
                                Value.Add("Error", "ExecutePath could not be null or empty");
                            }

                            break;
                        }
                    case CommandType.Test_Connection:
                        {
                            try
                            {
                                if (CommandValue.TryGetValue("ProcessId", out string ProcessId))
                                {
                                    if ((ExplorerProcess?.Id).GetValueOrDefault() != Convert.ToInt32(ProcessId))
                                    {
                                        ExplorerProcess = Process.GetProcessById(Convert.ToInt32(ProcessId));
                                    }
                                }

                                if (PipeCommandReadController == null && CommandValue.TryGetValue("PipeCommandWriteId", out string PipeCommandWriteId))
                                {
                                    PipeCommandReadController = new NamedPipeReadController(Convert.ToUInt32(ExplorerProcess.Id), $"Explorer_NamedPipe_{PipeCommandWriteId}");
                                    PipeCommandReadController.OnDataReceived += PipeReadController_OnDataReceived;
                                }

                                if (PipeCommandWriteController == null && CommandValue.TryGetValue("PipeCommandReadId", out string PipeCommandReadId))
                                {
                                    PipeCommandWriteController = new NamedPipeWriteController(Convert.ToUInt32(ExplorerProcess.Id), $"Explorer_NamedPipe_{PipeCommandReadId}");
                                }

                                if (PipeProgressWriterController == null && CommandValue.TryGetValue("PipeProgressReadId", out string PipeProgressReadId))
                                {
                                    PipeProgressWriterController = new NamedPipeWriteController(Convert.ToUInt32(ExplorerProcess.Id), $"Explorer_NamedPipe_{PipeProgressReadId}");
                                }
                            }
                            catch (Exception ex)
                            {
                                LogTracer.Log(ex);
                            }


                            Value.Add(Enum.GetName(typeof(CommandType), CommandType.Test_Connection), string.Empty);

                            break;
                        }
                    case CommandType.PasteRemoteFile:
                        {
                            string Path = Convert.ToString(CommandValue["Path"]);

                            if (await Helper.ExecuteOnSTAThreadAsync(() =>
                            {
                                RemoteDataObject Rdo = new RemoteDataObject(Clipboard.GetDataObject());

                                foreach (RemoteDataObject.DataPackage Package in Rdo.GetRemoteData())
                                {
                                    try
                                    {
                                        if (Package.ItemType == RemoteDataObject.StorageType.File)
                                        {
                                            string DirectoryPath = System.IO.Path.GetDirectoryName(Path);

                                            if (!Directory.Exists(DirectoryPath))
                                            {
                                                Directory.CreateDirectory(DirectoryPath);
                                            }

                                            string UniqueName = StorageController.GenerateUniquePath(System.IO.Path.Combine(Path, Package.Name));

                                            using (FileStream Stream = new FileStream(UniqueName, FileMode.CreateNew))
                                            {
                                                Package.ContentStream.CopyTo(Stream);
                                            }
                                        }
                                        else
                                        {
                                            string DirectoryPath = System.IO.Path.Combine(Path, Package.Name);

                                            if (!Directory.Exists(DirectoryPath))
                                            {
                                                Directory.CreateDirectory(DirectoryPath);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Package.Dispose();
                                    }
                                }
                            }))
                            {
                                Value.Add("Success", string.Empty);
                            }
                            else
                            {
                                Value.Add("Error", "Clipboard is empty or could not get the content");
                            }

                            break;
                        }
                    case CommandType.GetThumbnailOverlay:
                        {
                            string Path = Convert.ToString(CommandValue["Path"]);

                            Value.Add("Success", JsonSerializer.Serialize(StorageController.GetThumbnailOverlay(Path)));

                            break;
                        }
                    case CommandType.SetAsTopMostWindow:
                        {
                            string PackageFamilyName = Convert.ToString(CommandValue["PackageFamilyName"]);
                            uint WithPID = Convert.ToUInt32(CommandValue["WithPID"]);

                            if (Helper.GetUWPWindowInformation(PackageFamilyName, WithPID) is WindowInformation Info && !Info.Handle.IsNull)
                            {
                                User32.SetWindowPos(Info.Handle, new IntPtr(-1), 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE);
                                Value.Add("Success", string.Empty);
                            }
                            else
                            {
                                Value.Add("Error", "Could not found the window handle");
                            }

                            break;
                        }
                    case CommandType.RemoveTopMostWindow:
                        {
                            string PackageFamilyName = Convert.ToString(CommandValue["PackageFamilyName"]);
                            uint WithPID = Convert.ToUInt32(CommandValue["WithPID"]);

                            if (Helper.GetUWPWindowInformation(PackageFamilyName, WithPID) is WindowInformation Info && !Info.Handle.IsNull)
                            {
                                User32.SetWindowPos(Info.Handle, new IntPtr(-2), 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE);
                                Value.Add("Success", string.Empty);
                            }
                            else
                            {
                                Value.Add("Error", "Could not found the window handle");
                            }

                            break;
                        }
                    case CommandType.AppServiceCancelled:
                        {
                            ExitLocker.Set();
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Value.Clear();
                Value.Add("Error", ex.Message);
            }

            return Value;
        }

        private static void SetWindowsZPosition(Process OtherProcess, IEnumerable<HWND> WindowsBeforeStartup)
        {
            try
            {
                void SetWindowsPosFallback(IEnumerable<HWND> WindowsBeforeStartup)
                {
                    foreach (HWND Handle in Helper.GetCurrentWindowsHandle().Except(WindowsBeforeStartup))
                    {
                        User32.SetWindowPos(Handle, User32.SpecialWindowHandles.HWND_TOPMOST, 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE);
                        User32.SetWindowPos(Handle, User32.SpecialWindowHandles.HWND_NOTOPMOST, 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE);
                    }
                }

                if (OtherProcess.WaitForInputIdle(5000))
                {
                    IntPtr MainWindowHandle = IntPtr.Zero;

                    for (int i = 0; i < 10 && !OtherProcess.HasExited; i++)
                    {
                        OtherProcess.Refresh();

                        if (OtherProcess.MainWindowHandle.CheckIfValidPtr())
                        {
                            MainWindowHandle = OtherProcess.MainWindowHandle;
                            break;
                        }
                        else
                        {
                            Thread.Sleep(500);
                        }
                    }

                    if (MainWindowHandle.CheckIfValidPtr())
                    {
                        bool IsSuccess = true;

                        uint ExecuteThreadId = User32.GetWindowThreadProcessId(MainWindowHandle, out _);
                        uint ForegroundThreadId = User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out _);
                        uint CurrentThreadId = Kernel32.GetCurrentThreadId();

                        if (ForegroundThreadId != ExecuteThreadId)
                        {
                            User32.AttachThreadInput(ForegroundThreadId, CurrentThreadId, true);
                            User32.AttachThreadInput(ForegroundThreadId, ExecuteThreadId, true);
                        }

                        IsSuccess &= User32.ShowWindow(MainWindowHandle, ShowWindowCommand.SW_SHOWNORMAL);
                        IsSuccess &= User32.SetWindowPos(MainWindowHandle, User32.SpecialWindowHandles.HWND_TOPMOST, 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE);
                        IsSuccess &= User32.SetWindowPos(MainWindowHandle, User32.SpecialWindowHandles.HWND_NOTOPMOST, 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE);
                        IsSuccess &= User32.SetForegroundWindow(MainWindowHandle);

                        if (Helper.GetUWPWindowInformation(Package.Current.Id.FamilyName, (uint)(ExplorerProcess?.Id).GetValueOrDefault()) is WindowInformation UwpWindow && !UwpWindow.Handle.IsNull)
                        {
                            IsSuccess &= User32.SetWindowPos(UwpWindow.Handle, MainWindowHandle, 0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOSIZE | User32.SetWindowPosFlags.SWP_NOACTIVATE);
                        }

                        if (ForegroundThreadId != ExecuteThreadId)
                        {
                            User32.AttachThreadInput(ForegroundThreadId, CurrentThreadId, false);
                            User32.AttachThreadInput(ForegroundThreadId, ExecuteThreadId, false);
                        }

                        if (!IsSuccess)
                        {
                            LogTracer.Log("Could not switch to window because noraml method failed, use fallback function");
                            SetWindowsPosFallback(WindowsBeforeStartup);
                        }
                    }
                    else
                    {
                        LogTracer.Log("Could not switch to window because MainWindowHandle is invalid, use fallback function");
                        SetWindowsPosFallback(WindowsBeforeStartup);
                    }
                }
                else
                {
                    LogTracer.Log("Could not switch to window because WaitForInputIdle is timeout after 5000ms, use fallback function");
                    SetWindowsPosFallback(WindowsBeforeStartup);
                }
            }
            catch (InvalidOperationException ex)
            {
                LogTracer.Log(ex, "Error: WaitForInputIdle threw an exception.");
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"Error: {nameof(SetWindowsZPosition)} threw an exception, message: {ex.Message}");
            }
        }

        private static void AliveCheck(object state)
        {
            try
            {
                if ((ExplorerProcess?.HasExited).GetValueOrDefault())
                {
                    ExitLocker.Set();
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"{nameof(AliveCheck)} threw an exception, message: {ex.Message}");
            }
        }

        private static Process CreateNewProcessAsElevated<T>(T Data) where T : IElevationData
        {
            using (Process CurrentProcess = Process.GetCurrentProcess())
            {
                string TempFilePath = Path.Combine(Path.GetTempPath(), $"Template_{CurrentProcess.Id}");

                using (StreamWriter Writer = File.CreateText(TempFilePath))
                {
                    Writer.WriteLine(Data.GetType().FullName);
                    Writer.WriteLine(JsonSerializer.Serialize(Data));
                }

                return Process.Start(new ProcessStartInfo
                {
                    FileName = CurrentProcess.MainModule.FileName,
                    Arguments = $"/ExecuteAdminOperation \"{TempFilePath.Encrypt("W8aPHu7MGOGA5x5x")}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
        }

        private static List<string> LoadEnvironmentStringPaths()
        {
            var envStrings = new List<string>();

            foreach (DictionaryEntry special in Environment.GetEnvironmentVariables())
            {
                var path = special.Value.ToString();
                if (Directory.Exists(path))
                {
                    envStrings.Add((string)special.Key);
                }
            }

            return envStrings;
        }


    }
}
