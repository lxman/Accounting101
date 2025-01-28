using CommunityToolkit.Mvvm.Input;
using MauiInterface.Models;

namespace MauiInterface.PageModels;

public interface IProjectTaskPageModel
{
    IAsyncRelayCommand<ProjectTask> NavigateToTaskCommand { get; }
    bool IsBusy { get; }
}