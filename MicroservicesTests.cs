using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microservices.Common.Exceptions;
using Microservices.ExternalServices.Authorization;
using Microservices.ExternalServices.Authorization.Types;
using Microservices.ExternalServices.Billing;
using Microservices.ExternalServices.Billing.Types;
using Microservices.ExternalServices.CatDb;
using Microservices.ExternalServices.CatDb.Types;
using Microservices.ExternalServices.Database;
using Microservices.Types;
using NUnit.Framework;

namespace Microservices
{
    [TestFixture]
    public class Services
    {
        public class CatInfoService : ICatInfoService
        {
            public readonly List<CatInfo> Cats = new();

            public async Task<CatInfo> FindByBreedIdAsync(Guid id, CancellationToken cancellationToken)
            {
                return Cats.Find(c => c.BreedId == id);
            }

            public async Task<CatInfo[]> FindByBreedIdAsync(Guid[] ids, CancellationToken cancellationToken)
            {
                return ids.Select(guid => Cats.Find(c => c.BreedId == guid)).ToArray();
            }

            public async Task<CatInfo> FindByBreedNameAsync(string breed, CancellationToken cancellationToken)
            {
                return Cats.Find(c => c.BreedName == breed);
            }

            public void AddCatInfo(CatInfo catInfo)
            {
                Cats.Add(catInfo);
            }

            public int Count => Cats.Count;

            public static CatInfoService GetCatInfoService()
            {
                var catInfoService = new CatInfoService();
                var cat1 = new CatInfo {BreedId = Guid.NewGuid(), BreedName = "russian", Photo = null};
                var cat2 = new CatInfo {BreedId = Guid.NewGuid(), BreedName = "ukranian", Photo = null};
                var cat3 = new CatInfo {BreedId = Guid.NewGuid(), BreedName = "american", Photo = null};
                var cat4 = new CatInfo {BreedId = Guid.NewGuid(), BreedName = "poland", Photo = null};
                catInfoService.AddCatInfo(cat1);
                catInfoService.AddCatInfo(cat2);
                catInfoService.AddCatInfo(cat3);
                catInfoService.AddCatInfo(cat4);

                return catInfoService;
            }
        }
        
        public class Db : IDatabase
        {
            public class CatForDb : Cat, IEntityWithId<Guid>
            {}

            public DbCollection<CatForDb, Guid> CatsCollection = new();

            public IDatabaseCollection<TDocument, TId> GetCollection<TDocument, TId>(string collectionName) where TDocument : class, IEntityWithId<TId>
            {
                return (IDatabaseCollection<TDocument, TId>) CatsCollection;
            }
            
            public class DbCollection<TDocument, TId> : IDatabaseCollection<TDocument, TId> where TDocument : class, IEntityWithId<TId>
            {
                public List<TDocument> Cats = new();
                public async Task<TDocument> FindAsync(TId id, CancellationToken cancellationToken)
                {
                    return Cats.Find(c => c.Id.Equals(id));
                }

                public Task<List<TDocument>> FindAsync(Func<TDocument, bool> filter, CancellationToken cancellationToken)
                {
                    throw new NotImplementedException();
                }

                public async Task WriteAsync(TDocument document, CancellationToken cancellationToken)
                {
                    Cats.Add(document);
                }

                public async Task DeleteAsync(TId id, CancellationToken cancellationToken)
                {
                    Cats.RemoveAll(c => c.Id.Equals(id));
                }
            }
        }
        
        public class AuthorizationService : IAuthorizationService
        {
            public async Task<AuthorizationResult> AuthorizeAsync(string sessionId, CancellationToken cancellationToken)
            {
                return new AuthorizationResult {IsSuccess = true, UserId = Guid.NewGuid()};
            }
        }
        
        public class BillingService : IBillingService
        {
            public List<Product> Products = new();

            public async Task AddProductAsync(Product product, CancellationToken cancellationToken)
            {
                Products.Add(product);
            }

            public async Task<List<Product>> GetProductsAsync(int skip, int limit, CancellationToken cancellationToken)
            {
                return Products.Skip(skip).Take(limit).ToList();
            }

            public async Task<Product> GetProductAsync(Guid id, CancellationToken cancellationToken)
            {
                return Products.Find(p => p.Id == id);
            }

            public Task<Bill> SellProductAsync(Guid id, decimal price, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }

        [Test]
        public void SimpleAddCatTest()
        {
            var catInfoService = CatInfoService.GetCatInfoService();
            Assert.AreEqual(4, catInfoService.Count);
        }

        [Test]
        public async Task FindByBreedIdTest()
        {
            var catsInfo = CatInfoService.GetCatInfoService();
            var cat = await catsInfo.FindByBreedIdAsync(catsInfo.Cats[0].BreedId, new CancellationToken());
            Assert.AreEqual(cat.BreedId, catsInfo.Cats[0].BreedId);
        }
    }

    [TestFixture]
    public class AddCatTests
    {
        public class AuthorizationServiceThrowsConnectionException : IAuthorizationService
        {
            public Task<AuthorizationResult> AuthorizeAsync(string sessionId, CancellationToken cancellationToken)
            {
                throw new ConnectionException();
            }
        }
        
        public class AuthorizationServiceThrowsAuthorizationException : IAuthorizationService
        {
            public async Task<AuthorizationResult> AuthorizeAsync(string sessionId, CancellationToken cancellationToken)
            {
                return new AuthorizationResult {IsSuccess = false, UserId = Guid.NewGuid()};
            }
        }
        
        [Test]
        public void AddCatThrowsConnectionExceptionTest()
        {
            var catShelter = new CatShelterService(new Services.Db(), new AuthorizationServiceThrowsConnectionException(),
                new Services.BillingService(), Services.CatInfoService.GetCatInfoService(), null);
            var addRequest = new AddCatRequest {Breed = "russian", Name = "asya", Photo = null};
            Assert.ThrowsAsync<ConnectionException>(() =>
                catShelter.AddCatAsync(Guid.NewGuid().ToString(), addRequest, new CancellationToken()));
        }

        [Test]
        public void AddCatThrowsAuthorizationExceptionTest()
        {
            var catShelter = new CatShelterService(new Services.Db(), new AuthorizationServiceThrowsAuthorizationException(),
                new Services.BillingService(), Services.CatInfoService.GetCatInfoService(), null);
            var addRequest = new AddCatRequest {Breed = "russian", Name = "asya", Photo = null};
            Assert.ThrowsAsync<AuthorizationException>(() =>
                catShelter.AddCatAsync(Guid.NewGuid().ToString(), addRequest, new CancellationToken()));
        }

        [Test]
        public void DebugAddTest()
        {
            var catShelter = new CatShelterService(new Services.Db(), new Services.AuthorizationService(),
                new Services.BillingService(), Services.CatInfoService.GetCatInfoService(), null);
            var addRequest = new AddCatRequest {Breed = "russian", Name = "asya", Photo = null};
            catShelter.AddCatAsync(Guid.NewGuid().ToString(), addRequest, new CancellationToken());
        }
    }
}