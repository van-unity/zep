using System;
using Zep.Core.Application.Ports;

namespace Zep.Core.Application
{

internal sealed class IndexRefreshScheduler
{
	private readonly Action<bool> _doRefresh;

	private readonly IDeferredCallScheduler _scheduler;

	private readonly Action _processCallback;

	private bool _isScheduled;

	private bool _scheduledShouldEmitStatusToast;

	private bool _isDisposed;

	internal IndexRefreshScheduler(Action<bool> doRefresh, IDeferredCallScheduler scheduler)
	{
		_doRefresh = doRefresh ?? throw new ArgumentNullException(nameof(doRefresh));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_processCallback = Process;
	}

	internal void Request(bool emitStatusToast)
	{
		if (!_isDisposed)
		{
			_scheduledShouldEmitStatusToast |= emitStatusToast;
			if (!_isScheduled)
			{
				_isScheduled = true;
				_scheduler.Schedule(_processCallback);
			}
		}
	}

	internal void Cancel()
	{
		if (_isScheduled)
		{
			_scheduler.Cancel(_processCallback);
			_isScheduled = false;
			_scheduledShouldEmitStatusToast = false;
		}
	}

	internal void Dispose()
	{
		_isDisposed = true;
		Cancel();
	}

	private void Process()
	{
		if (_isDisposed)
		{
			_isScheduled = false;
			_scheduledShouldEmitStatusToast = false;
			return;
		}
		bool scheduledShouldEmitStatusToast = _scheduledShouldEmitStatusToast;
		_isScheduled = false;
		_scheduledShouldEmitStatusToast = false;
		_doRefresh(scheduledShouldEmitStatusToast);
	}
}

}
