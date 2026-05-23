using System;
using System.ComponentModel;
using System.Reflection;

namespace AISupportAnalysisPlatform.Extensions
{
    public static class EnumExtensions
    {
        public static string GetLocalizedDescription(this Enum value)
        {
            if (value == null) return string.Empty;

            var field = value.GetType().GetField(value.ToString());
            if (field == null) return value.ToString();

            var attribute = Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) as DescriptionAttribute;
            
            return attribute != null ? attribute.Description : value.ToString();
        }
    }
}
