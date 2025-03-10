﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.CodeStyle
{
    internal static class FadingOptions
    {
        public static readonly PerLanguageOption2<bool> FadeOutUnusedImports = new("FadingOptions_FadeOutUnusedImports", defaultValue: true);
        public static readonly PerLanguageOption2<bool> FadeOutUnreachableCode = new("FadingOptions_FadeOutUnreachableCode", defaultValue: true);
    }
}
