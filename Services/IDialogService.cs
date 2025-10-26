using System.Threading.Tasks;

namespace GoombaCast.Services;

/*
 * Interface for dialog services. 
 * Add our dialog methods here.
 */

public interface IDialogService
{
    Task ShowSettingsDialogAsync();
}