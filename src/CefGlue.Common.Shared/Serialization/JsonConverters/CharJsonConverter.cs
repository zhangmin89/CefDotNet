using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xilium.CefGlue.Common.Shared.Serialization
{
    internal class CharJsonConverter : JsonConverter<char>
    {
        public override char Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new InvalidOperationException();
        }

        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(DataMarkers.StringMarker + value);
        }
    }
}
