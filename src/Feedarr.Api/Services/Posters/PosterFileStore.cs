namespace Feedarr.Api.Services.Posters;

public interface IPosterFileStore
{
    bool Exists(string fullPath);
    void Delete(string fullPath);
}

public sealed class PosterFileStore : IPosterFileStore
{
    public bool Exists(string fullPath) => File.Exists(fullPath);

    public void Delete(string fullPath) => File.Delete(fullPath);
}
