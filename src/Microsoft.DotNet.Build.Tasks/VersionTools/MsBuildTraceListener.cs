﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Diagnostics;
using System.Text;

namespace Microsoft.DotNet.Build.Tasks.VersionTools
{
    public class MsBuildTraceListener : TraceListener
    {
        private TaskLoggingHelper _log;
        private TraceEventType _eventType;
        private StringBuilder _partialLine = new StringBuilder();

        public MsBuildTraceListener(TaskLoggingHelper log, TraceEventType eventType)
        {
            _log = log;
            _eventType = eventType;

            Filter = new TraceEventTypeFilter
            {
                ShouldTraceType = eventType
            };
        }

        public override void Write(string message)
        {
            _partialLine.Append(message);
        }

        public override void WriteLine(string message)
        {
            string fullMessage = _partialLine + message;
            _partialLine.Clear();

            switch (_eventType)
            {
                case TraceEventType.Error:
                    _log.LogError(fullMessage);
                    break;
                case TraceEventType.Warning:
                    _log.LogWarning(fullMessage);
                    break;
                case TraceEventType.Critical:
                    _log.LogMessage(MessageImportance.High, fullMessage);
                    break;
                case TraceEventType.Information:
                    _log.LogMessage(MessageImportance.Normal, fullMessage);
                    break;
                case TraceEventType.Verbose:
                    _log.LogMessage(MessageImportance.Low, fullMessage);
                    break;
            }
        }

        private class TraceEventTypeFilter : TraceFilter
        {
            public TraceEventType ShouldTraceType { get; set; }

            public override bool ShouldTrace(
                TraceEventCache cache,
                string source,
                TraceEventType eventType,
                int id,
                string formatOrMessage,
                object[] args,
                object data1,
                object[] data)
            {
                return eventType == ShouldTraceType;
            }
        }
    }
}
