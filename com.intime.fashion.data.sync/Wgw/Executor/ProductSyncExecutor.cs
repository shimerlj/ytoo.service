﻿using System.Collections.Generic;
using com.intime.fashion.data.sync.Wgw.Request.Builder;
using com.intime.fashion.data.sync.Wgw.Request.Item;
using com.intime.fashion.data.sync.Wgw.Response.Processor;
using com.intime.jobscheduler.Job.Wgw;
using Common.Logging;
using System;
using System.Linq;
using Yintai.Hangzhou.Data.Models;

namespace com.intime.fashion.data.sync.Wgw.Executor
{
    public class ProductSyncExecutor:ExecutorBase
    {
        private int _succeedCount;
        private int _failedCount;
        private int _totalCount;
        public ProductSyncExecutor(DateTime benchTime, ILog logger) : base(benchTime, logger)
        {
        }
        protected override int SucceedCount
        {
            get { return _succeedCount; }
        }

        protected override int FailedCount
        {
            get { return _failedCount; }
        }

        protected override void ExecuteCore(dynamic parameter = null)
        {
            int size = parameter ?? 20;

            this.UploadProducts(size);
        }

        private void QueryNewItems(Action<IQueryable<ProductEntity>> callback)
        {
            using (var db = DbContextHelper.GetDbContext())
            {
                var products =
                    db.Set<ProductEntity>()
                        .Where(
                            p =>
                                p.IsHasImage
                                && p.Is4Sale.HasValue
                                && p.Is4Sale.Value
                                && p.Status == 1
                                && !db.Map4Products.Any(
                                    m => m.Channel == ConstValue.WGW_CHANNEL_NAME && m.ProductId == p.Id)
                        );
                        //.Join(
                        //    db.Set<BrandEntity>()
                        //        .Join(db.Set<Map4Brand>().Where(m => m.Channel == ConstValue.WGW_CHANNEL_NAME),
                        //            b => b.Id, m => m.BrandId, (b, m) => b), p => p.Brand_Id, b => b.Id, (p, m) => p)
                        //.Join(
                        //    db.Set<ProductMapEntity>()
                        //        .Join(
                        //            db.Set<CategoryEntity>()
                        //                .Join(
                        //                    db.Set<Map4Category>().Where(m => m.Channel == ConstValue.WGW_CHANNEL_NAME),
                        //                    c => c.ExCatCode, m => m.CategoryCode, (c, m) => c), pm => pm.ChannelCatId,
                        //            c => c.ExCatId, (pm, c) => pm),
                        //    p => p.Id, pm => pm.ProductId, (p, pm) => p);
                if (callback != null)
                    callback(products);
            }
        }

        private void QueryUpdatedItems(Action<IQueryable<ProductEntity>> callback)
        {
            using (var db = DbContextHelper.GetDbContext())
            {
                var items =
                    db.Products.Where(p=>p.UpdatedDate > BenchTime).Join(db.Map4Products.Where(pm => pm.Channel == ConstValue.WGW_CHANNEL_NAME), p => p.Id,
                        pm => pm.ProductId, (p, pm) => new {p, pm})
                        .Where(@p1 => @p1.p.IsHasImage && @p1.p.UpdatedDate > @p1.pm.UpdateDate)
                        .Select(@t1 => @t1.p);
                if (callback != null)
                {
                    callback(items);
                }
            }
        }

        /// <summary>
        /// 同步商品
        /// </summary>
        /// <param name="pageSize">每次上传商品个数</param>
        private void UploadProducts(int pageSize)
        {
            QueryNewItems(products=>_totalCount = products.Count());

            int lastCursor = 0;
            int cursor = 0;
            while (cursor < _totalCount)
            {
                List<ProductEntity> oneTimeList = null;
                QueryNewItems(products =>
                {
                    oneTimeList = products.Where(a => a.Id > lastCursor).OrderBy(a => a.Id).Take(pageSize).ToList();
                });
                _succeedCount += oneTimeList.Count(Upload);
                
                cursor += pageSize;
                lastCursor = oneTimeList.Max(t => t.Id);
            }

            lastCursor = 0;
            cursor = 0;
            int updateCount = 0;
            QueryUpdatedItems(items => updateCount = items.Count());

            while (cursor < updateCount)
            {
                List<ProductEntity> oneTimeList = null;
                QueryUpdatedItems(
                    items =>
                    {
                        oneTimeList = items.Where(i=>i.Id >  lastCursor).OrderBy(p => p.Id).Take(pageSize).ToList();
                    });
                _succeedCount += oneTimeList.Count(Update);
                cursor += pageSize;
                lastCursor = oneTimeList.Max(t => t.Id);
            }

            _totalCount += updateCount;
        }

        /// <summary>
        /// 更新已经映射至微购物的商品映射信息
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private bool Update(ProductEntity item)
        {
            try
            {
                var builder = RequestParamsBuilderFactory.CreateBuilder(new UpdateItemRequest());
                var result = Client.Execute<dynamic>(builder.BuildParameters(item));
                if (result.errorCode == 0)
                {
                    _succeedCount += 1;
                    using (var db = DbContextHelper.GetDbContext())
                    {
                        var map4Product =
                            db.Map4Products.FirstOrDefault(
                                m => m.ProductId == item.Id && m.Channel == ConstValue.WGW_CHANNEL_NAME);

                        if (map4Product == null)
                        {
                            throw new WgwSyncException(string.Format("未映射商品至微购物 {0} ID=({1})",item.Name, item.Id));
                        }

                        map4Product.Status = item.Is4Sale.HasValue && item.Is4Sale.Value && item.IsHasImage && item.Status == 1
                            ? 1
                            : 0;
                        map4Product.UpdateDate = DateTime.Now;
                        db.SaveChanges();
                        return true;
                    }
                }

                Logger.Error(string.Format("Failed to modify product {0}({1}) Error Message: {2}", item.Name, item.Id, result.errorMessage));
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Failed to modify product {0}({1}) Error Message: {2}", item.Name, item.Id, ex.Message));
            }
            _failedCount += 1;
            return false;
        }

        private bool Upload(ProductEntity item)
        {
            try
            {
                var builder = RequestParamsBuilderFactory.CreateBuilder(new AddItemRequest());
                var result = Client.Execute<dynamic>(builder.BuildParameters(item));
                if (result.errorCode == 0)
                {
                    var processor = ProcessorFactory.CreateProcessor<ItemResponseProcessor>();
                    if (processor.Process(result, item.Id))
                    {
                        _succeedCount += 1;
                        return true;
                    }
                    Logger.Error(string.Format("Failed to upload product to wgw {0}({1}) Error Message: {2}", item.Name,
                        item.Id, processor.ErrorMessage));
                }
                else //if (result.errorCode == 50005) //图片读取问题导致的失败实际上商品有可能已经上传成功需要补救下
                {
                    var request = new QueryItemListRequest();
                    request.Put("defStockId",item.Id);
                    request.Put("startIndex",0);
                    request.Put("pageSize",1);
                    request.Put("orderType","7");
                    var rsp = Client.Execute<dynamic>(request);
                    if (rsp.errorCode == 0)
                    {
                        var ps = ProcessorFactory.CreateProcessor<QueryItemListResponseProcessor>();
                        if (ps.Process(rsp, item))
                        {
                            _succeedCount += 1;
                            return true;
                        }
                        Logger.Error(ps.ErrorMessage);
                    }
                    else
                    {
                        Logger.Error(string.Format("Failed to upload product to wgw {0}({1}) Error Message: {2}", item.Name,
                        item.Id, result.errorMessage));
                    }
                }
                //else
                //{
                //    Logger.Error(string.Format("Failed to upload product to wgw {0}({1}) Error Message: {2}", item.Name,
                //        item.Id, result.errorMessage));
                //}

            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Failed to upload product {0}({1}) Error Message: {2}", item.Name, item.Id, ex.Message));
            }
            _failedCount += 1;
            return false;
        }
    }
}
