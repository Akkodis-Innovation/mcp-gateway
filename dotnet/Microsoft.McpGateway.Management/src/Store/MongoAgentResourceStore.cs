// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using MongoDB.Driver;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// MongoDB implementation of the agent resource store.
    /// </summary>
    public class MongoAgentResourceStore : IAgentResourceStore
    {
        private readonly IMongoCollection<AgentResource> _collection;
        private readonly ILogger<MongoAgentResourceStore> _logger;
        private readonly Task _initializationTask;

        public MongoAgentResourceStore(IMongoDatabase database, string collectionName, ILogger<MongoAgentResourceStore> logger)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            _collection = database.GetCollection<AgentResource>(collectionName);
            _logger = logger;
            _initializationTask = EnsureIndexesAsync();
        }

        public async Task<AgentResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection.Find(x => x.Id == name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UpsertAsync(AgentResource agent, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);

            var filter = Builders<AgentResource>.Filter.Eq(x => x.Id, agent.Id);
            await _collection.ReplaceOneAsync(
                filter,
                agent,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            await _collection.DeleteOneAsync(x => x.Id == name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<AgentResource>> ListAsync(CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection
                .Find(Builders<AgentResource>.Filter.Empty)
                .SortBy(x => x.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task EnsureIndexesAsync()
        {
            try
            {
                var idIndex = new CreateIndexModel<AgentResource>(
                    Builders<AgentResource>.IndexKeys.Ascending(x => x.Id),
                    new CreateIndexOptions { Unique = true, Name = "ux_id" });

                await _collection.Indexes.CreateOneAsync(idIndex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create indexes for agent collection");
                throw;
            }
        }
    }
}
