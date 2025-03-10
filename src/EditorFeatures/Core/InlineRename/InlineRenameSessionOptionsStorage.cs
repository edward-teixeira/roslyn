﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.InlineRename
{
    internal static class InlineRenameSessionOptionsStorage
    {
        public static readonly Option2<bool> RenameOverloads = new("InlineRenameSessionOptions_RenameOverloads", defaultValue: false);
        public static readonly Option2<bool> RenameInStrings = new("InlineRenameSessionOptions_RenameInStrings", defaultValue: false);
        public static readonly Option2<bool> RenameInComments = new("InlineRenameSessionOptions_RenameInComments", defaultValue: false);
        public static readonly Option2<bool> RenameFile = new("InlineRenameSessionOptions_RenameFile", defaultValue: true);
        public static readonly Option2<bool> PreviewChanges = new("InlineRenameSessionOptions_PreviewChanges", defaultValue: false);
        public static readonly Option2<bool> RenameAsynchronously = new("InlineRenameSessionOptions_RenameAsynchronously", defaultValue: true);
    }
}
