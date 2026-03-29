// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using MongoDB.Driver;

namespace Microsoft.McpGateway.Management.Store
{
    public class MongoAdapterResourceStore : IAdapterResourceStore
    {
        private readonly IMongoCollection<AdapterResource> _collection;
        private readonly ILogger<MongoAdapterResourceStore> _logger;
        private readonly Task _initializationTask;

        public MongoAdapterResourceStore(IMongoDatabase database, string collectionName, ILogger<MongoAdapterResourceStore> logger)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            _collection = database.GetCollection<AdapterResource>(collectionName);
            _logger = logger;
            _initializationTask = EnsureIndexesAsync();
        }

        public async Task<AdapterResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection.Find(x => x.Id == name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UpsertAsync(AdapterResource adapter, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);

            var filter = Builders<AdapterResource>.Filter.Eq(x => x.Id, adapter.Id);
            await _collection.ReplaceOneAsync(
                filter,
                adapter,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            await _collection.DeleteOneAsync(x => x.Id == name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<AdapterResource>> ListAsync(CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection
                .Find(Builders<AdapterResource>.Filter.Empty)
                .SortBy(x => x.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task EnsureIndexesAsync()
        {
            try
            {
                var idIndex = new CreateIndexModel<AdapterResource>(
                    Builders<AdapterResource>.IndexKeys.Ascending(x => x.Id),
                    new CreateIndexOptions { Unique = true, Name = "ux_id" });

                await _collection.Indexes.CreateOneAsync(idIndex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create indexes for adapter collection");
                throw;
            }
        }
    }
}
