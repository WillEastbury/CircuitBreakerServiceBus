using System.Linq;
using System.Threading;
namespace CircuitBreaker
{
    public class CircuitHistory
    {
        private int NextWritingPositionInArray = 0;
        private int MaxHistoryLength = 100;
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1,1);
        private RequestStatusType[] requests;
        public CircuitHistory(int RequestCapacity)
        {   
            MaxHistoryLength = RequestCapacity;
            requests = new RequestStatusType[MaxHistoryLength];
        }
        public void AddNewRequestStatus(RequestStatusType requestStatus)
        {
            _semaphore.Wait();
            requests[NextWritingPositionInArray] = requestStatus;
            // Update the next write position for the ring buffer, cycling to zero if we move to MaxHistoryLength
            NextWritingPositionInArray++;
            if(NextWritingPositionInArray >= (MaxHistoryLength))
            {
                // Roll around to the beginning of the array
                NextWritingPositionInArray = 0;
            }
            _semaphore.Release();
        }
        public int GetStatsByRequestType(RequestStatusType rst) => requests.Where(x => x == rst).Count();

        public int GetAllRequestCount() => requests.Count();
    }
}