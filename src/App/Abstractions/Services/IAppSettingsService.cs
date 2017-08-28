﻿using System;

namespace Bit.App.Abstractions
{
    public interface IAppSettingsService
    {
        bool Locked { get; set; }
        DateTime LastActivity { get; set; }
        DateTime LastCacheClear { get; set; }
        bool AutofillPersistNotification { get; set; }
        bool AutofillPasswordField { get; set; }
        string SecurityStamp { get; set; }
        string BaseUrl { get; set; }
        string WebVaultUrl { get; set; }
        string ApiUrl { get; set; }
        string IdentityUrl { get; set; }
    }
}