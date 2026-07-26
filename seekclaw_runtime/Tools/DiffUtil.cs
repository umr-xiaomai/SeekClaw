using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;

namespace SeekClaw.Runtime.Tools;

/// <summary>Produces compact unified diffs for file edits (rendered by the terminal UI).</summary>
public static class DiffUtil
{
    public static string Unified(string oldText, string newText, string filePath, int context = 3)
    {
        var diff = Differ.Instance.CreateDiffs(oldText, newText, false, false, LineChunker.Instance);

        if (diff.DiffBlocks.Count == 0) return "";

        IReadOnlyList<string> oldLines = diff.PiecesOld;
        IReadOnlyList<string> newLines = diff.PiecesNew;
        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{filePath}");
        sb.AppendLine($"+++ b/{filePath}");

        foreach (var block in diff.DiffBlocks)
        {
            var oldStart = Math.Max(0, block.DeleteStartA - context);
            var oldEnd = Math.Min(oldLines.Count, block.DeleteStartA + block.DeleteCountA + context);
            var newStart = Math.Max(0, block.InsertStartB - context);
            var newEnd = Math.Min(newLines.Count, block.InsertStartB + block.InsertCountB + context);

            sb.AppendLine($"@@ -{oldStart + 1},{oldEnd - oldStart} +{newStart + 1},{newEnd - newStart} @@");

            for (var i = oldStart; i < block.DeleteStartA; i++)
                sb.AppendLine(" " + oldLines[i]);
            for (var i = block.DeleteStartA; i < block.DeleteStartA + block.DeleteCountA; i++)
                sb.AppendLine("-" + oldLines[i]);
            for (var i = block.InsertStartB; i < block.InsertStartB + block.InsertCountB; i++)
                sb.AppendLine("+" + newLines[i]);
            for (var i = block.DeleteStartA + block.DeleteCountA; i < oldEnd; i++)
                sb.AppendLine(" " + oldLines[i]);
        }

        return sb.ToString();
    }
}
