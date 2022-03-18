using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microservices.Common.Exceptions;
using Microservices.ExternalServices.Authorization;
using Microservices.ExternalServices.Authorization.Types;
using Microservices.ExternalServices.Billing;
using Microservices.ExternalServices.Billing.Types;
using Microservices.ExternalServices.CatDb;
using Microservices.ExternalServices.CatExchange;
using Microservices.ExternalServices.Database;
using Microservices.Types;

namespace Microservices
{
    public class CatShelterService : ICatShelterService
    {
        private class CatForDb : Cat, IEntityWithId<Guid>
        {
            public readonly HashSet<Guid> FavouriteUsers = new();

            public CatForDb(){}
            public CatForDb(Guid id)
            {
                Id = id;
            }
        }

        private readonly IDatabase _database;
        private readonly IAuthorizationService _authorizationService;
        private readonly IBillingService _billingService;
        private readonly ICatInfoService _catInfoService;
        private readonly ICatExchangeService _catExchangeService;

        public CatShelterService(
            IDatabase database,
            IAuthorizationService authorizationService,
            IBillingService billingService,
            ICatInfoService catInfoService,
            ICatExchangeService catExchangeService)
        {
            _database = database;
            _authorizationService = authorizationService;
            _billingService = billingService;
            _catInfoService = catInfoService;
            _catExchangeService = catExchangeService;
        }

        public async Task<List<Cat>> GetCatsAsync(string sessionId, int skip, int limit,
            CancellationToken cancellationToken)
        {
            await CheckAuthorization(sessionId, cancellationToken);
            var productsList = await TrySendRequest(() => _billingService.GetProductsAsync(skip, limit, cancellationToken));
            var result = new List<Cat>();
            var catsCollection = GetCatsInShelterCollection();
            
            foreach (var product in productsList)
            {
                var currentCat = await catsCollection.FindAsync(product.Id, cancellationToken);
                if (currentCat != null) result.Add(currentCat);
            }

            return result;
        }

        public async Task AddCatToFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            var authorizationResult = await CheckAuthorization(sessionId, cancellationToken);
            var favouriteCatsCollection = GetCatsInShelterCollection();
            var favourite = await favouriteCatsCollection
                .FindAsync(catId, cancellationToken) ?? new CatForDb(catId);
            
            favourite.FavouriteUsers.Add(authorizationResult.UserId);
            favouriteCatsCollection.WriteAsync(favourite, cancellationToken);
        }

        public async Task<List<Cat>> GetFavouriteCatsAsync(string sessionId, CancellationToken cancellationToken)
        {
            var authorizationResult = await CheckAuthorization(sessionId, cancellationToken);
            var catsCollection = GetCatsInShelterCollection();
            var favouriteCats = await catsCollection
                .FindAsync(cat => cat.FavouriteUsers.Contains(authorizationResult.UserId), cancellationToken);

            var result = new List<Cat>();
            foreach (var favouriteCat in favouriteCats)
            {
                var isSold = await _billingService.GetProductAsync(favouriteCat.Id, cancellationToken) == null;
                if (isSold) catsCollection.DeleteAsync(favouriteCat.Id, cancellationToken);
                else result.Add(favouriteCat);
            }
            
            return result;
        }

        public async Task DeleteCatFromFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            var authorizationResult = await CheckAuthorization(sessionId, cancellationToken);
            var favouriteCats = GetCatsInShelterCollection();
            var unFavouriteCat = await favouriteCats
                .FindAsync(catId, cancellationToken);
            unFavouriteCat?.FavouriteUsers.Remove(authorizationResult.UserId);
        }

        public async Task<Bill> BuyCatAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            await CheckAuthorization(sessionId, cancellationToken);
            var catCollection = GetCatsInShelterCollection();
            if (await TrySendRequest(() => _billingService.GetProductAsync(catId, cancellationToken)) == null)
                throw new InvalidRequestException();
            var buyingCat = await catCollection.FindAsync(catId, cancellationToken);
            if (buyingCat == null) return new Bill();
            var price = buyingCat.Price;
            var bill = await TrySendRequest(() => _billingService.SellProductAsync(catId, price, cancellationToken));

            return bill;
        }

        public async Task<Guid> AddCatAsync(string sessionId, AddCatRequest request,
            CancellationToken cancellationToken)
        {
            var authorizationResult = await CheckAuthorization(sessionId, cancellationToken);
            if (request.Name == null || request.Breed == null) throw new InvalidRequestException();

            var catInfo = await TrySendRequest(() => _catInfoService.FindByBreedNameAsync(request.Breed, cancellationToken));
            var productId = Guid.NewGuid();

            TrySendRequest(() => _billingService.AddProductAsync(
                new Product {Id = productId, BreedId = catInfo.BreedId},
                cancellationToken));

            var priceHistory =
                TrySendRequest(() => _catExchangeService.GetPriceInfoAsync(catInfo.BreedId, cancellationToken))
                    .Result
                    .Prices
                    .Select(p => (p.Date, p.Price))
                    .OrderBy(p => p.Date)
                    .ToList();

            GetCatsInShelterCollection().WriteAsync(new CatForDb
            {
                Id = productId,
                AddedBy = authorizationResult.UserId,
                Breed = catInfo.BreedName,
                BreedId = catInfo.BreedId,
                BreedPhoto = catInfo.Photo,
                CatPhoto = request.Photo,
                Name = request.Name,
                Price = priceHistory.Count == 0 ? 1000 : priceHistory.Last().Price,
                Prices = priceHistory
            }, cancellationToken);

            return productId;
        }

        private async Task<AuthorizationResult> CheckAuthorization(string sessionId, CancellationToken cancellationToken)
        {
            var authorizationResult = await TrySendRequest(() =>
                _authorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess) throw new AuthorizationException();
            return authorizationResult;
        }

        private static TResult TrySendRequest<TResult>(Func<TResult> func)
        {
            var connectionErrorsCount = 0;
            TResult requestResult = default;
            while (connectionErrorsCount < 2)
            {
                try
                {
                    requestResult = func();
                    break;
                }
                catch (Exception e)
                {
                    if (e is not ConnectionException) throw;
                    if (++connectionErrorsCount == 2) throw new InternalErrorException();
                }
            }
            
            return requestResult;
        }

        private IDatabaseCollection<CatForDb, Guid> GetCatsInShelterCollection() => 
            _database.GetCollection<CatForDb, Guid>("CatsInShelter");
    }
}