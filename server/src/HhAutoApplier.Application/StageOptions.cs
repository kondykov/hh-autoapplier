namespace HhAutoApplier.Application;

// Метка тестового стенда (#124): локальный stage на копии боевой базы. Пусто — боевой сервер, интерфейсы без отметок.
// Задаётся STAGE_LABEL в .env стенда; веб берёт её из GET /api/version, бот — отсюда
public class StageOptions
{
    public const string Section = "Stage";

    public string? Label { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Label);

    // Первая строка экрана и уведомлений бота на стенде
    public string BotLine => $"🧪 STAGE · {Label!.Trim()}";
}
