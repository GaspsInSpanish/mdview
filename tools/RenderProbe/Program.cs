using MdView.Rendering;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: RenderProbe <input.md> <output.html>");
    return 1;
}

var markdown = await File.ReadAllTextAsync(args[0]);
var title = Path.GetFileNameWithoutExtension(args[0]);
var document = Renderer.RenderDocument(markdown, title);
await File.WriteAllTextAsync(args[1], document);
return 0;
