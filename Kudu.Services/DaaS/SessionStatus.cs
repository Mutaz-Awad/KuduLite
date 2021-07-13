﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Kudu.Services.Performance
{
    /// <summary>
    /// 
    /// </summary>
    
    [JsonConverter(typeof(StringEnumConverter))]
    public enum Status
    {
        Active,
        Started,
        Complete
    }
}
