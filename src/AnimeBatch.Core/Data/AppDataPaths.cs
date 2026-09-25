namespace AnimeBatch.Core.Data;

/// <summary>
/// Pastas de dados do aplicativo em %LOCALAPPDATA%\AnimeBatch — sobrevivem a reinstalação
/// e troca do executável (o pacote portátil pode ser apagado/substituído sem perder nada):
/// o banco SQLite (animebatch.db) e os arquivos de capítulos editados (chapters-edits\).
/// </summary>
public static class AppDataPaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeBatch");

    public static string DbPath => Path.Combine(Root, "animebatch.db");

    public static string ChaptersEditsDir => Path.Combine(Root, "chapters-edits");
}
