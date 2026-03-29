// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using MongoDB.Driver;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// MongoDB implementation of the tool resource store.
    /// </summary>
    public class MongoToolResourceStore : IToolResourceStore
    {
        private readonly IMongoCollection<ToolResource> _collection;
        private readonly ILogger<MongoToolResourceStore> _logger;
        private readonly Task _initializationTask;

        public MongoToolResourceStore(IMongoDatabase database, string collectionName, ILogger<MongoToolResourceStore> logger)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            _collection = database.GetCollection<ToolResource>(collectionName);
            _logger = logger;
            _initializationTask = EnsureIndexesAsync();
        }

        public async Task<ToolResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection.Find(x => x.Id == name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UpsertAsync(ToolResource tool, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);

            var filter = Builders<ToolResource>.Filter.Eq(x => x.Id, tool.Id);
            await _collection.ReplaceOneAsync(
                filter,
                tool,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            await _collection.DeleteOneAsync(x => x.Id == name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<ToolResource>> ListAsync(CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection
                .Find(Builders<ToolResource>.Filter.Empty)
                .SortBy(x => x.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task EnsureIndexesAsync()
        {
            try
            {
                var idIndex = new CreateIndexModel<ToolResource>(
                    Builders<ToolResource>.IndexKeys.Ascending(x => x.Id),
                    new CreateIndexOptions { Unique = true, Name = "ux_id" });

                await _collection.Indexes.CreateOneAsync(idIndex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create indexes for tool collection");
                throw;
            }
        }
    }
}
