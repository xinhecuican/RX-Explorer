﻿using System;
using System.Linq;
using System.Threading.Tasks;

namespace RX_Explorer.Class
{
    public sealed class OperationListDeleteUndoModel : OperationListUndoModel
    {
        public override string FromDescription
        {
            get
            {
                return string.Empty;
            }
        }

        public override string ToDescription
        {
            get
            {
                if (UndoFrom.Length > 5)
                {
                    return $"{Globalization.GetString("TaskList_To_Label")}: {Environment.NewLine}{string.Join(Environment.NewLine, UndoFrom.Take(5))}{Environment.NewLine}({UndoFrom.Length - 5} {Globalization.GetString("TaskList_More_Items")})...";
                }
                else
                {
                    return $"{Globalization.GetString("TaskList_To_Label")}: {Environment.NewLine}{string.Join(Environment.NewLine, UndoFrom)}";
                }
            }
        }

        public string[] UndoFrom { get; }

        public override bool CanBeCancelled => true;

        public override async Task PrepareSizeDataAsync()
        {
            ulong TotalSize = 0;

            foreach (FileSystemStorageItemBase Item in await FileSystemStorageItemBase.OpenInBatchAsync(UndoFrom))
            {
                switch (Item)
                {
                    case FileSystemStorageFolder Folder:
                        {
                            TotalSize += await Folder.GetFolderSizeAsync();
                            break;
                        }
                    case FileSystemStorageFile File:
                        {
                            TotalSize += File.Size;
                            break;
                        }
                }
            }

            Calculator = new ProgressCalculator(TotalSize);
        }

        public OperationListDeleteUndoModel(string[] UndoFrom, EventHandler OnCompleted = null, EventHandler OnErrorHappended = null, EventHandler OnCancelled = null) : base(OnCompleted, OnErrorHappended, OnCancelled)
        {
            if (UndoFrom.Any((Path) => string.IsNullOrWhiteSpace(Path)))
            {
                throw new ArgumentNullException(nameof(UndoFrom), "Parameter could not be empty or null");
            }

            this.UndoFrom = UndoFrom;
        }
    }
}
