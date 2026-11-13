using System;

namespace Zep.Core.Application.Ports
{

public interface IDeferredCallScheduler
{
	void Schedule(Action action);

	void Cancel(Action action);
}

}
