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
            public DateTime AddedTime { get; set; }
        }

        private class FavouriteCat : IEntityWithId<Guid>
        {
            public Guid Id { get; set; }
            public List<Guid> UsersId { get; set; }

            public FavouriteCat(Guid id, Guid userId)
            {
                Id = id;
                UsersId = new List<Guid> {userId};
            }

            public void AddFavouriteUser(Guid userId)
            {
                UsersId.Add(userId);
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
            var favouriteCatsCollection = GetFavouriteCatsCollection();
            var favourite = await favouriteCatsCollection
                .FindAsync(catId, cancellationToken);

            if (favourite == null) favourite = new FavouriteCat(catId, authorizationResult.UserId);
            else favourite.AddFavouriteUser(authorizationResult.UserId);
            
            favouriteCatsCollection.WriteAsync(favourite, cancellationToken);
        }

        public async Task<List<Cat>> GetFavouriteCatsAsync(string sessionId, CancellationToken cancellationToken)
        {
            var authorizationResult = await CheckAuthorization(sessionId, cancellationToken);
            var result = new List<Cat>();
            var favCatsCollection = GetFavouriteCatsCollection();
            var favouriteCats = await favCatsCollection
                .FindAsync(cat => cat.UsersId.Contains(authorizationResult.UserId), cancellationToken);
            var catsInShelterCollection = GetCatsInShelterCollection();
            foreach (var favouriteCat in favouriteCats)
            {
                var currentFavourite = await catsInShelterCollection.FindAsync(favouriteCat.Id, cancellationToken);
                var isSold = await _billingService.GetProductAsync(currentFavourite.Id, cancellationToken) == null;
                if (isSold)
                {
                    favCatsCollection.DeleteAsync(currentFavourite.Id, cancellationToken);
                    catsInShelterCollection.DeleteAsync(currentFavourite.Id, cancellationToken);
                }
                else result.Add(await catsInShelterCollection.FindAsync(favouriteCat.Id, cancellationToken));
            }

            return result;
        }

        public async Task DeleteCatFromFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            var authorizationResult = await CheckAuthorization(sessionId, cancellationToken);
            var favouriteCats = GetFavouriteCatsCollection();
            var unFavouriteCatList = favouriteCats
                .FindAsync(cat => cat.Id == catId, cancellationToken)
                .Result;
            if (unFavouriteCatList.Count == 0) return;
            var unFavouriteCat = unFavouriteCatList.First();
            if (unFavouriteCat.UsersId.Count == 1) favouriteCats.DeleteAsync(catId, cancellationToken);
            else
            {
                unFavouriteCat.UsersId.Remove(authorizationResult.UserId);
                favouriteCats.WriteAsync(unFavouriteCat, cancellationToken);
            }
        }

        public async Task<Bill> BuyCatAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            await CheckAuthorization(sessionId, cancellationToken);
            var catCollection = GetCatsInShelterCollection();
            if (await TrySendRequest(() => _billingService.GetProductAsync(catId, cancellationToken)) == null)
                throw new InvalidRequestException();
            var buyingCat = await TrySendRequest(() => catCollection.FindAsync(catId, cancellationToken));
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
                Prices = priceHistory,
                AddedTime = DateTime.Now
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

        private IDatabaseCollection<FavouriteCat, Guid> GetFavouriteCatsCollection()
        {
            return _database.GetCollection<FavouriteCat, Guid>("FavouriteCats"); 
        }

        private IDatabaseCollection<CatForDb, Guid> GetCatsInShelterCollection()
        {
            return _database.GetCollection<CatForDb, Guid>("CatsInShelter");
        }
        
        //TODO: создать класс для БД, в которую можно записывать Cat, который наследуется до CatForDb
    }
}