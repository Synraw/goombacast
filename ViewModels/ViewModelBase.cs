using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace GoombaCast.ViewModels
{
    /// <summary>
    /// Base class for all view models providing common functionality
    /// </summary>
    public abstract partial class ViewModelBase : ObservableObject, IDisposable
    {
        private bool _disposed;

        /// <summary>
        /// Gets or sets whether the view model is currently busy
        /// </summary>
        [ObservableProperty]
        private bool _isBusy;

        /// <summary>
        /// Gets or sets the busy message displayed during operations
        /// </summary>
        [ObservableProperty]
        private string _busyMessage = string.Empty;

        /// <summary>
        /// Executes an async operation while managing busy state
        /// </summary>
        /// <param name="operation">The async operation to execute</param>
        /// <param name="busyMessage">Optional message to display while busy</param>
        protected async System.Threading.Tasks.Task ExecuteBusyAsync(
            Func<System.Threading.Tasks.Task> operation,
            string busyMessage = "Processing...")
        {
            if (IsBusy) return;

            IsBusy = true;
            BusyMessage = busyMessage;

            try
            {
                await operation().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        /// <summary>
        /// Executes an async operation with error handling and busy state management
        /// </summary>
        /// <param name="operation">The async operation to execute</param>
        /// <param name="errorMessage">Optional custom error message</param>
        /// <param name="busyMessage">Optional message to display while busy</param>
        /// <returns>True if the operation succeeded, false if an error occurred</returns>
        protected async System.Threading.Tasks.Task<bool> ExecuteSafeAsync(
            Func<System.Threading.Tasks.Task> operation,
            string? errorMessage = null,
            string busyMessage = "Processing..."
        )
        {
            if (IsBusy) return false;

            IsBusy = true;
            BusyMessage = busyMessage;

            try
            {
                await operation().ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                OnError(errorMessage ?? "An error occurred", ex);
                return false;
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        /// <summary>
        /// Called when an error occurs during an operation
        /// Override in derived classes to provide custom error handling
        /// </summary>
        /// <param name="message">The error message</param>
        /// <param name="exception">The exception that occurred</param>
        protected virtual void OnError(string message, Exception exception)
        {
            // Default implementation - derived classes can override
            System.Diagnostics.Debug.WriteLine($"Error: {message} - {exception.Message}");
        }

        /// <summary>
        /// Disposes of managed resources
        /// Override to dispose of resources in derived classes
        /// </summary>
        /// <param name="disposing">True if called from Dispose(), false if from finalizer</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Override in derived classes to dispose of resources
                _disposed = true;
            }
        }

        /// <summary>
        /// Disposes of managed resources
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
