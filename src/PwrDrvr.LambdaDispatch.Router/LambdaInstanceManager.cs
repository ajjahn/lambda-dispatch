namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

public class LambdaInstanceManager
{
  private readonly ILogger<LambdaInstanceManager> _logger = LoggerInstance.CreateLogger<LambdaInstanceManager>();

  private readonly LeastOutstandingQueue _leastOutstandingQueue;

  private volatile int _runningInstanceCount = 0;

  private volatile int _desiredInstanceCount = 0;

  private volatile int _startingInstanceCount = 0;

  private readonly int _maxConcurrentCount;

  /// <summary>
  /// Used to lookup instances by ID
  /// This allows associating the connecitons with their owning lambda instance
  /// </summary>
  private readonly ConcurrentDictionary<string, LambdaInstance> _instances = new();

  public LambdaInstanceManager(int maxConcurrentCount)
  {
    _maxConcurrentCount = maxConcurrentCount;
    _leastOutstandingQueue = new(maxConcurrentCount);
  }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection)
  {
    // Return an available instance or start a new one if none are available
    var gotConnection = _leastOutstandingQueue.TryGetLeastOustandingConnection(out var dequeuedConnection);
    connection = dequeuedConnection;

    return gotConnection;
  }

  public bool ValidateLambdaId(string lambdaId)
  {
    return _instances.ContainsKey(lambdaId);
  }

  public async Task<LambdaConnection?> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, bool immediateDispatch = false)
  {
    // Get the instance for the lambda
    if (_instances.TryGetValue(lambdaId, out var instance))
    {
      // Add the connection to the instance
      // The instance will eventually get rebalanced in the least outstanding queue
      var connection = instance.AddConnection(request, response, immediateDispatch);

      // Check where this instance is in the least outstanding queue
      if (!immediateDispatch)
      {
        _leastOutstandingQueue.ReinstateFullInstance(instance);
      }

      return connection;
    }

    Console.WriteLine($"Connection added to Lambda Instance {lambdaId} that does not exist - closing with 1001");

    // Close the connection
    try
    {
      response.StatusCode = 1001;
      await response.CompleteAsync();
      response.Body.Close();
      request.Body.Close();
    }
    catch (Exception ex)
    {
      Console.WriteLine("Exception closing down connection to Lambda");
      Console.WriteLine(ex.Message);
    }

    return null;
  }

  // TODO: This should project excess capacity and not use 100% of max capacity at all times
  // TODO: This should not start new instances for pending requests at a 1/1 ratio but rather something less than that
  public async Task UpdateDesiredCapacity(int pendingRequests, int runningRequests)
  {
    _logger.LogInformation("UpdateDesiredCapacity - BEFORE - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredCount {_desiredCount}, _startingCount {_startingCount}", pendingRequests, runningRequests, _desiredInstanceCount, _startingInstanceCount);

    var cleanPendingRequests = Math.Max(pendingRequests, 0);
    var cleanRunningRequests = Math.Max(runningRequests, 0);

    // Calculate the desired count
    var totalDesiredRequestCapacity = cleanPendingRequests + cleanRunningRequests;
    var desiredCount = (int)Math.Ceiling((double)totalDesiredRequestCapacity / _maxConcurrentCount);

    // Special case for 0 pending or running
    if (cleanPendingRequests == 0 && cleanRunningRequests == 0)
    {
      desiredCount = 0;
    }

    // Try to set the new desired count
    // If we end up setting this to a smaller number we won't
    // stop the instances right away, but only when they disconnect
    Interlocked.Exchange(ref _desiredInstanceCount, desiredCount);

    // Start instances if needed
    // We might stop if another thread updates this to be smaller while we loop
    while (_runningInstanceCount + _startingInstanceCount < _desiredInstanceCount)
    {
      // Start a new instance
      await this.StartNewInstance(true);
    }


    _logger.LogInformation("UpdateDesiredCapacity - AFTER - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredCount {_desiredCount}, _startingCount {_startingCount}", pendingRequests, runningRequests, _desiredInstanceCount, _startingInstanceCount);
  }

  /// <summary>
  /// Start a new LambdaInstance and increment the desired count
  /// </summary>
  /// <returns></returns>
  private async Task StartNewInstance(bool incrementDesiredCount = false)
  {
    // If we get called we're starting a new instance
    if (incrementDesiredCount)
    {
      Interlocked.Increment(ref _desiredInstanceCount);
    }

    Interlocked.Increment(ref _startingInstanceCount);

    // Start a new LambdaInstance and add it to the list
    var instance = new LambdaInstance(_maxConcurrentCount);

    // Add the instance to the collection
    _instances.TryAdd(instance.Id, instance);

    instance.OnOpen += (instance) =>
    {
      // We always decrement the starting count
      Interlocked.Decrement(ref _startingInstanceCount);

      _logger.LogInformation("LambdaInstance {instanceId} opened", instance.Id);

      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired
      Interlocked.Increment(ref _runningInstanceCount);

      // Add the instance to the least outstanding queue
      _leastOutstandingQueue.AddInstance(instance);
    };

    instance.OnInvocationComplete += async (instance) =>
    {
      Interlocked.Decrement(ref _runningInstanceCount);

      _logger.LogInformation("LambdaInstance {instanceId} invocation complete, _desiredCount {_desiredCount}, _runningCount {_runningCount} (after decrement)", instance.Id, _desiredInstanceCount, _runningInstanceCount);

      // Remove this instance from the collection
      _instances.TryRemove(instance.Id, out _);

      // The instance will already be marked as closing

      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired

      if (_runningInstanceCount + _startingInstanceCount < _desiredInstanceCount)
      {
        // We need to start a new instance
        await this.StartNewInstance();
      }
    };

    // This is only async because of the ENI IP lookup
    await instance.Start();
  }
}
