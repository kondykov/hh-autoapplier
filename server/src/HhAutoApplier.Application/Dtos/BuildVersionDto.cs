namespace HhAutoApplier.Application.Dtos;

// Stage — метка тестового стенда (#124), на боевом сервере null; необязательное поле, контракт API не меняет
public record BuildVersionDto(string Version, string Commit, int ApiVersion, string? Stage = null);
