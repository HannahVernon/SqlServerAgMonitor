namespace SqlAgMonitor.Services;

public interface ILayoutStateService
{
    WindowLayoutState Load();
    void Save(WindowLayoutState state);
}
