﻿using System;
using System.Threading.Tasks;

namespace RX_Explorer.Class
{
    public sealed class OperationListNewUndoModel : OperationListUndoModel
    {
        public override string FromDescription
        {
            get
            {
                return $"{Globalization.GetString("TaskList_From_Label")}: {Environment.NewLine}{UndoFrom}";
            }
        }

        public override string ToDescription
        {
            get
            {
                return string.Empty;
            }
        }

        public string UndoFrom { get; }

        public override bool CanBeCancelled => false;

        public override Task PrepareSizeDataAsync()
        {
            Calculator = new ProgressCalculator(0);
            return Task.CompletedTask;
        }

        public OperationListNewUndoModel(string UndoFrom, EventHandler OnCompleted = null, EventHandler OnErrorHappended = null, EventHandler OnCancelled = null) : base(OnCompleted, OnErrorHappended, OnCancelled)
        {
            if (string.IsNullOrWhiteSpace(UndoFrom))
            {
                throw new ArgumentNullException(nameof(UndoFrom), "Parameter could not be empty or null");
            }

            this.UndoFrom = UndoFrom;
        }
    }
}
