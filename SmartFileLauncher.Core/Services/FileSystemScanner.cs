 using System.IO;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Search;
namespace SmartFileLauncher.Core.Services;
/// <summary>
/// Recursively scans a starting directory building:
/// - N-ary tree (FileSystemNode)
/// - Inverted index (tokens from names)
/// - Metadata dictionary (path -> metadata)
/// Complexity: O(N * a) where N = number of filesystem entries, a = avg tokens per name.
/// </summary>
public class FileSystemScanner {
    private readonly ITokenizer _tokenizer;
    public FileSystemScanner(ITokenizer tokenizer) { _tokenizer = tokenizer; }
    public FileSystemNode ScanDesktop(out InvertedIndex invertedIndex, out Dictionary<string, FileMetadata> metadataMap) {
        // Try multiple possible Desktop locations
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        
        // If Desktop is empty or doesn't exist, try alternative paths
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) {
            // Try OneDrive Desktop
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string oneDriveDesktop = Path.Combine(userProfile, "OneDrive", "Masaüstü");
            if (Directory.Exists(oneDriveDesktop)) {
                desktop = oneDriveDesktop;
            } else {
                // Try English OneDrive Desktop
                oneDriveDesktop = Path.Combine(userProfile, "OneDrive", "Desktop");
                if (Directory.Exists(oneDriveDesktop)) {
                    desktop = oneDriveDesktop;
                } else {
                    // Fallback to classic Desktop
                    desktop = Path.Combine(userProfile, "Desktop");
                }
            }
        }
        
        invertedIndex = new InvertedIndex();
        metadataMap = new Dictionary<string, FileMetadata>();
        var root = new FileSystemNode(Path.GetFileName(desktop) ?? "Desktop", desktop, true);
        ScanRecursive(desktop, root, invertedIndex, metadataMap);
        return root;
    }
    private void ScanRecursive(string path, FileSystemNode parent, InvertedIndex index, Dictionary<string, FileMetadata> metaMap) {
        try {
            foreach (var dir in Directory.GetDirectories(path)) {
                var dirName = Path.GetFileName(dir);
                var node = new FileSystemNode(dirName, dir, true);
                parent.AddChild(node);
                
                // Index folder name tokens
                IndexName(node, index);
                
                metaMap[dir] = new FileMetadata { LastWriteTime = Directory.GetLastWriteTime(dir) };
                ScanRecursive(dir, node, index, metaMap);
            }
            foreach (var file in Directory.GetFiles(path)) {
                var fi = new FileInfo(file);
                var node = new FileSystemNode(fi.Name, file, false) { 
                    Metadata = new FileMetadata { 
                        SizeBytes = fi.Length, 
                        LastWriteTime = fi.LastWriteTime,
                        CreatedTime = fi.CreationTime
                    } 
                };
                parent.AddChild(node);
                IndexName(node, index);
                metaMap[file] = node.Metadata!;
            }
        } catch (UnauthorizedAccessException) { /* Skip */ } catch (IOException) { /* Skip */ }
    }
    private void IndexName(FileSystemNode node, InvertedIndex index) {
        foreach (var token in _tokenizer.Tokenize(node.Name)) { index.Add(token, node); }
    }
}