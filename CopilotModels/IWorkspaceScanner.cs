namespace CopilotModels
{
    public interface IWorkspaceScanner
    {
        WorkspaceContext ScanWorkspace(string goal);
    }
}