namespace MdView.Serving;

public interface IFileDialogProvider
{
    Task<string?> ShowOpenAsync(string initialDirectory, CancellationToken cancellationToken);
    Task<string?> ShowSaveAsAsync(string initialDirectory, string suggestedFileName,
        CancellationToken cancellationToken);
}
