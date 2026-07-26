namespace SmartFileLauncher.Core.Search;
public interface ITokenizer {
    IEnumerable<string> Tokenize(string input);
}