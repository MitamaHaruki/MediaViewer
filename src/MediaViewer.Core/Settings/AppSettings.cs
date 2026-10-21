using System.Text.Json;
using System.Text.Json.Serialization;
using MediaViewer.Core.Navigation;

namespace MediaViewer.Core.Settings;

/// <summary>更新を適用するタイミング（仕様 §10.2-7）。</summary>
public enum UpdateApplyMode
{
    /// <summary>アプリの終了時に自動で適用する（既定）。</summary>
    OnExit,

    /// <summary>確認してから適用する（延期できる）。</summary>
    AskEachTime,
}

/// <summary>ウィンドウの通常時（最大化していないとき）の位置・大きさと、最大化していたか。</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }
}

/// <summary>自動更新の設定（仕様 §10、§12）。</summary>
public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public bool AutoDownload { get; set; } = true;

    [JsonConverter(typeof(LenientEnumConverter<UpdateApplyMode>))]
    public UpdateApplyMode ApplyMode { get; set; } = UpdateApplyMode.OnExit;
    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>この版が知らない項目（新しい版が書いたもの）を、上書き保存で消さないために保持する。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// ユーザー設定（仕様 §12）。初期版の設定は少数に留める。色管理は品質要件のため設定に含めない（常に有効）。
/// フルスクリーンは保存せず、常に通常表示で起動する。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const double DefaultVolume = 0.8;
    public static readonly int[] SeekStepChoices = [5, 10, 30];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonConverter(typeof(LenientEnumConverter<NavigationFilter>))]
    public NavigationFilter NavigationFilter { get; set; } = NavigationFilter.All;
    public double Volume { get; set; } = DefaultVolume;
    public bool IsMuted { get; set; }

    /// <summary>動画・音声を終端で先頭に戻して繰り返す（初期値 OFF。OFF なら終端で停止、仕様 §7.2）。</summary>
    public bool RepeatEnabled { get; set; }
    public int SeekStepSeconds { get; set; } = 5;
    public WindowPlacement? Window { get; set; }
    public UpdateSettings Update { get; set; } = new();

    /// <summary>この版が知らない項目（新しい版が書いたもの）を、上書き保存で消さないために保持する。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>範囲外・不正な値を安全な値に直す（壊れた設定や手編集への備え）。</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(NavigationFilter)) NavigationFilter = NavigationFilter.All;
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : DefaultVolume;
        if (!SeekStepChoices.Contains(SeekStepSeconds)) SeekStepSeconds = 5;
        Update ??= new UpdateSettings();
        if (!Enum.IsDefined(Update.ApplyMode)) Update.ApplyMode = UpdateApplyMode.OnExit;
        if (Window is { Width: < 200 } or { Height: < 150 }) Window = null;
    }
}

/// <summary>
/// 列挙型を文字列で読み書きする。知らない値（新しい版が追加した値など）は既定値として読み、
/// 1項目のせいで設定全体を壊れた扱いにしない。
/// </summary>
internal sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            var value = (TEnum)Enum.ToObject(typeof(TEnum), number);
            if (Enum.IsDefined(value)) return value;
        }
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
