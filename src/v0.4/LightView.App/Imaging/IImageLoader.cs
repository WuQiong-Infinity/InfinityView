namespace LightView.App.Imaging;

public interface IImageLoader
{
    LoadedImage Load(string path, CancellationToken cancellationToken);
}

public interface IImagePrefetcher
{
    bool TryPrefetch(string path, CancellationToken cancellationToken);
}

public interface IProgressiveImageLoader : IImageLoader
{
    LoadedImage LoadPreview(string path, int maximumEdge, CancellationToken cancellationToken);
    bool TryPrefetchPreview(string path, int maximumEdge, CancellationToken cancellationToken);
}
