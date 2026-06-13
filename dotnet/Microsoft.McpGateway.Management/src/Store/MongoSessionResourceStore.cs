// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using MongoDB.Driver;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// MongoDB implementation of the session resource store.
    /// </summary>
    public class MongoSessionResourceStore : ISessionResourceStore
    {
        private readonly IMongoCollection<SessionResource> _collection;
        private readonly ILogger<MongoSessionResourceStore> _logger;
        private readonly Task _initializationTask;

        public MongoSessionResourceStore(IMongoDatabase database, string collectionName, ILogger<MongoSessionResourceStore> logger)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            _collection = database.GetCollection<SessionResource>(collectionName);
            _logger = logger;
            _initializationTask = EnsureIndexesAsync();
        }

        public async Task<SessionResource?> TryGetAsync(string id, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UpsertAsync(SessionResource session, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);

            var filter = Builders<SessionResource>.Filter.Eq(x => x.Id, session.Id);
            await _collection.ReplaceOneAsync(
                filter,
                session,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            await _collection.DeleteOneAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<SessionResource>> ListAsync(CancellationToken cancellationToken)
        {
            await _initializationTask.ConfigureAwait(false);
            return await _collection
                .Find(Builders<SessionResource>.Filter.Empty)
                .SortBy(x => x.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task EnsureIndexesAsync()
        {
            try
            {
                var idIndex = new CreateIndexModel<SessionResource>(
                    Builders<SessionResource>.IndexKeys.Ascending(x => x.Id),
                    new CreateIndexOptions { Unique = true, Name = "ux_id" });

                await _collection.Indexes.CreateOneAsync(idIndex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create indexes for session collection");
                throw;
            }
        }
    }
}
