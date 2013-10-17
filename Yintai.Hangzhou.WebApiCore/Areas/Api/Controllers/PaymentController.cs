﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;

using Com.Alipay;
using System.Data.Entity;
using Yintai.Architecture.Framework.ServiceLocation;
using Yintai.Hangzhou.Data.Models;
using System.Transactions;
using Yintai.Architecture.Common.Data.EF;
using Newtonsoft.Json;
using Yintai.Hangzhou.Model.Enums;
using Yintai.Hangzhou.Repository.Contract;
using Yintai.Hangzhou.Contract.DTO.Request;
using Yintai.Hangzhou.Contract.DTO.Response;
using com.intime.fashion.common.Wxpay;
using Yintai.Hangzhou.Contract.Customer;
using Yintai.Hangzhou.Contract.DTO.Request.Customer;
using Yintai.Hangzhou.Service.Logic;
using Yintai.Hangzhou.Model;


namespace Yintai.Hangzhou.WebApiCore.Areas.Api.Controllers
{
    public class PaymentController : Controller
    {
        private IEFRepository<OrderTransactionEntity> _orderTranRepo;
        private IEFRepository<PaymentNotifyLogEntity> _paymentNotifyRepo;
        private IOrderRepository _orderRepo;
        private IOrderLogRepository _orderlogRepo;
        private ICustomerDataService _customerService;

        public PaymentController(IEFRepository<OrderTransactionEntity> ordTranRepo
            , IEFRepository<PaymentNotifyLogEntity> paynotiRepo
            , IOrderRepository orderRepo
            , IOrderLogRepository orderLogRepo,
            ICustomerDataService customerService)
        {
            _orderTranRepo = ordTranRepo;
            _paymentNotifyRepo = paynotiRepo;
            _orderRepo = orderRepo;
            _orderlogRepo = orderLogRepo;
            _customerService = customerService;
        }

        public ActionResult Notify()
        {
            SortedDictionary<string, string> sPara = GetRequestPost();

            if (sPara.Count > 0)
            {
                Notify aliNotify = new Notify();
                bool verifyResult = aliNotify.Verify(sPara, Request.Form["notify_id"], Request.Form["sign"]);

                if (verifyResult)
                {
                    string out_trade_no = Request.Form["out_trade_no"];
                    string trade_no = Request.Form["trade_no"];

                    string trade_status = Request.Form["trade_status"];

                    var amount = decimal.Parse(Request.Form["total_fee"]);
                    if (Request.Form["trade_status"] == "TRADE_FINISHED")
                    {
                        var paymentEntity = Context.Set<OrderTransactionEntity>().Where(p => p.OrderNo == out_trade_no).FirstOrDefault();
                        var orderEntity = Context.Set<OrderEntity>().Where(o => o.OrderNo == out_trade_no).FirstOrDefault();
                        if (paymentEntity == null && orderEntity != null)
                        {
                            using (var ts = new TransactionScope())
                            {
                                _paymentNotifyRepo.Insert(new PaymentNotifyLogEntity()
                                {
                                    CreateDate = DateTime.Now,
                                    OrderNo = out_trade_no,
                                    PaymentCode = Config.PaymentCode,
                                    PaymentContent = JsonConvert.SerializeObject(sPara)
                                });

                                _orderTranRepo.Insert(new OrderTransactionEntity()
                                {
                                    Amount = amount,
                                    OrderNo = out_trade_no,
                                    CreateDate = DateTime.Now,
                                    IsSynced = false,
                                    PaymentCode = Config.PaymentCode,
                                    TransNo = trade_no
                                });
                                if (orderEntity.Status != (int)OrderStatus.Paid && orderEntity.TotalAmount<=amount)
                                {
                                    orderEntity.Status = (int)OrderStatus.Paid;
                                    orderEntity.UpdateDate = DateTime.Now;
                                    orderEntity.RecAmount = amount;
                                    _orderRepo.Update(orderEntity);

                                    _orderlogRepo.Insert(new OrderLogEntity()
                                    {
                                        CreateDate = DateTime.Now,
                                        CreateUser = 0,
                                        CustomerId = orderEntity.CustomerId,
                                        Operation = "支付订单",
                                        OrderNo = out_trade_no,
                                        Type = (int)OrderOpera.CustomerPay
                                    });
                                }
                                ts.Complete();
                            }
                        }

                    }
                    return Content("success");

                }
                else
                {
                    return Content("fail");
                }
            }
            else
            {
                return Content("无通知参数");
            }
        }

        public ActionResult NotifyWx()
        {
            Dictionary<string, string> sPara = GetQueryStringParams();

            if (sPara.Count > 0)
            {
                var requestSign = sPara["sign"];
                sPara.Remove("sign");
                var notifySigned = Util.NotifySign(sPara);

                if (string.Compare(requestSign, notifySigned, true) != 0)
                    return Content("fail");
                //external order no
                string out_trade_no = sPara["out_trade_no"];
                string trade_no = sPara["transaction_id"];
                int trade_status = int.Parse(sPara["trade_state"]);
                string bank_bill_no = sPara["bank_billno"];
                var amount = decimal.Parse(sPara["total_fee"]) / 100;
                if (trade_status == 0)
                {
                    var orderEntity = Context.Set<OrderEntity>().Join(Context.Set<Order2ExEntity>().Where(oe => oe.ExOrderNo == out_trade_no), o => o.OrderNo, i => i.OrderNo, (o, i) => o).FirstOrDefault();
                    var paymentEntity = Context.Set<OrderTransactionEntity>().Where(p => p.OrderNo == orderEntity.OrderNo && p.PaymentCode == WxPayConfig.PaymentCode).FirstOrDefault();

                    if (paymentEntity == null && orderEntity != null)
                    {
                        using (var ts = new TransactionScope())
                        {
                            _paymentNotifyRepo.Insert(new PaymentNotifyLogEntity()
                            {
                                CreateDate = DateTime.Now,
                                OrderNo = orderEntity.OrderNo,
                                PaymentCode = WxPayConfig.PaymentCode,
                                PaymentContent = JsonConvert.SerializeObject(sPara)
                            });

                            _orderTranRepo.Insert(new OrderTransactionEntity()
                            {
                                Amount = amount,
                                OrderNo = orderEntity.OrderNo,
                                CreateDate = DateTime.Now,
                                IsSynced = false,
                                PaymentCode = WxPayConfig.PaymentCode,
                                TransNo = trade_no
                            });
                            if (orderEntity.Status != (int)OrderStatus.Paid && orderEntity.TotalAmount <= amount)
                            {

                                orderEntity.Status = (int)OrderStatus.Paid;
                                orderEntity.UpdateDate = DateTime.Now;
                                orderEntity.RecAmount = amount;
                                _orderRepo.Update(orderEntity);

                                _orderlogRepo.Insert(new OrderLogEntity()
                                {
                                    CreateDate = DateTime.Now,
                                    CreateUser = 0,
                                    CustomerId = orderEntity.CustomerId,
                                    Operation = "支付订单",
                                    OrderNo = orderEntity.OrderNo,
                                    Type = (int)OrderOpera.CustomerPay
                                });
                            }
                            ts.Complete();
                        }
                    }

                }
                return Content("success");

            }
            else
            {
                return Content("fail");
            }
        }

        [HttpPost]
        public ActionResult WxPackage(WxPackageGetRequest request)
        {
            if (request == null)
            {
                return new XmlResult(composePackageError(r => r.RetErrMsg = "请求数据异常！"));
            }
            var productId = request.ProductId;
            var productIds = productId.Split('-');
            if (productIds.Length < 2)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "商品信息有误！"));

            if (string.Compare(productIds[0], "1", true) == 0)
                return doOrderPackage(productIds[1], request);
            else if (string.Compare(productIds[0], "2", true) == 0)
                return doProductPackage(request);
            else
                return new XmlResult(composePackageError(r => r.RetErrMsg = "订单类型有误！"));
        }

        private ActionResult doOrderPackage(string orderNo, WxPackageGetRequest request)
        {
            var orderEntity = Context.Set<OrderEntity>().Where(o => o.OrderNo == orderNo)
                            .Join(Context.Set<Order2ExEntity>(), o => o.OrderNo, i => i.OrderNo, (o, i) => new { O = o, OE = i })
                            .GroupJoin(Context.Set<OrderItemEntity>(), o => o.O.OrderNo, i => i.OrderNo, (o, i) => new { O = o.O, OE = o.OE, OI = i }).FirstOrDefault();

            if (orderEntity == null)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "订单不存在！"));
            if (orderEntity.O.Status != (int)OrderStatus.Create)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "订单状态无法支付！"));
            var orderItemEntity = orderEntity.OI.First();
            return new XmlResult(composePackageSuccess(r => r.Package = new WxPackage()
            {
                Body = orderItemEntity.ProductName,
                Attach = orderItemEntity.ProductDesc,
                OutTradeNo = orderEntity.OE.ExOrderNo,
                TotalFee = Util.Feng4Decimal(orderEntity.O.TotalAmount),
                TransportFee = Util.Feng4Decimal(orderEntity.O.ShippingFee ?? 0m),
                SPBill_Create_IP = clientIP(),
                Time_Start = orderEntity.O.CreateDate.ToString("yyyyMMddHHmmss"),
                Time_End = orderEntity.O.CreateDate.AddHours(4).ToString("yyyyMMddHHmmss")

            }));
        }

        private ActionResult doProductPackage(WxPackageGetRequest request)
        {
            var productIds = request.ProductId.Split('-');
            var productId = int.Parse(productIds[1]);
            var storeId = int.Parse(productIds[2]);
            var sectionId = int.Parse(productIds[3]);
            var linq = Context.Set<InventoryEntity>().Where(i => i.Id == productId)
                        .Join(Context.Set<ProductEntity>(), o => o.ProductId, i => i.Id, (o, i) => new { I = o, P = i })
                        .Join(Context.Set<ProductPropertyValueEntity>(), o => o.I.PColorId, i => i.Id, (o, i) => new { I = o.I, P = o.P, Color = i })
                        .Join(Context.Set<ProductPropertyValueEntity>(), o => o.I.PSizeId, i => i.Id, (o, i) => new { I = o.I, P = o.P, Color = o.Color, Size = i })
                        .FirstOrDefault();
            if (linq == null)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "商品不存在！"));

            if (linq.I.Amount < 1)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "商品库存不足！"));

            //step1: register user if not exist
            var userEntity = _customerService.OutSiteLogin2(new OutSiteLoginRequest()
            {
                OutsiteNickname = request.OpenId,
                OutsiteUid = request.OpenId,
                OutsiteType = (int)OutsiteType.WX
            });
            if (userEntity == null)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "用户信息有误！"));

            //step2: create order
            bool isCreateSuccess;
            var orderRequestModel = new OrderRequestModel()
            {
                InvoiceDetail = string.Empty,
                InvoiceTitle = string.Empty,
                NeedInvoice = false,
                Memo = string.Empty,
                Payment = new PaymentModel()
                {
                    PaymentCode = WxPayConfig.PaymentCode,
                    PaymentName = WxPayConfig.PaymentName,

                },
                Products = new List<OrderProductDetailRequest>() { 
                     new OrderProductDetailRequest(){
                          ProductId = linq.P.Id,
                          Desc = linq.P.Description,
                           Quantity = 1,
                            SectionId = sectionId,
                             StoreId = storeId,
                              Properties = new ProductPropertyValueRequest(){
                                 ColorValueId = linq.Color.Id,
                                  ColorValueName = linq.Color.ValueDesc,
                                   SizeValueId = linq.Size.Id,
                                    SizeValueName = linq.Size.ValueDesc
                              }
                     }
                }

            };
            var orderCreateResponse = OrderRule.Create(new OrderRequest()
            {
                Order = JsonConvert.SerializeObject(orderRequestModel),
                Channel = WxPayConfig.ORDER_SOURCE
            },
             new UserModel()
             {
                 Id = userEntity.Id,
                 Nickname = userEntity.Nickname
             }, out isCreateSuccess);
            if (!isCreateSuccess)
                return new XmlResult(composePackageError(r => r.RetErrMsg = "订单无法显示！"));
            var orderModel = orderCreateResponse.Data as OrderResponse;
            var orderNewEntity = Context.Set<OrderEntity>().Where(o => o.OrderNo == orderModel.OrderNo).FirstOrDefault();
            //step3: compose package message
            return new XmlResult(composePackageSuccess(r => r.Package = new WxPackage()
            {
                Body = linq.P.Name,
                Attach = linq.P.Description,
                OutTradeNo = orderModel.ExOrderNo,
                TotalFee = Util.Feng4Decimal(orderNewEntity.TotalAmount),
                TransportFee = Util.Feng4Decimal(orderNewEntity.ShippingFee ?? 0m),
                SPBill_Create_IP = clientIP(),
                Time_Start = orderNewEntity.CreateDate.ToString("yyyyMMddHHmmss"),
                Time_End = orderNewEntity.CreateDate.AddHours(4).ToString("yyyyMMddHHmmss")

            }));

        }

        private WxPackageGetResponse composePackageError(Action<WxPackageGetResponse> more)
        {
            var response = new WxPackageGetResponse()
                {
                    AppId = WxPayConfig.APP_ID,
                    TimeStamp = DateTime.Now.TicksOfWx().ToString(),
                    NonceStr = Util.Nonce(),
                    RetCode = (int)WxPayRetCode.RequestError,
                    RetErrMsg = "信息有误！",
                    SignMethod = WxPaySignMethod.SHA1,
                    Package = null
                };
            if (more != null)
                more(response);
            return response;
        }
        private WxPackageGetResponse composePackageSuccess(Action<WxPackageGetResponse> more)
        {
            var response = new WxPackageGetResponse()
            {
                AppId = WxPayConfig.APP_ID,
                TimeStamp = DateTime.Now.TicksOfWx().ToString(),
                NonceStr = Util.Nonce(),
                RetCode = (int)WxPayRetCode.Success,
                RetErrMsg = string.Empty,
                SignMethod = WxPaySignMethod.SHA1,
                Package = null
            };
            if (more != null)
                more(response);
            return response;
        }
        private string clientIP()
        {
            string ipAddress = Request.ServerVariables["HTTP_X_FORWARDED_FOR"];

            if (ipAddress == null || ipAddress.ToLower() == "unknown")
                ipAddress = Request.ServerVariables["REMOTE_ADDR"];

            return ipAddress;
        }
        private SortedDictionary<string, string> GetRequestPost()
        {
            int i = 0;
            SortedDictionary<string, string> sArray = new SortedDictionary<string, string>();
            NameValueCollection coll;
            //Load Form variables into NameValueCollection variable.
            coll = Request.Form;

            // Get names of all forms into a string array.
            String[] requestItem = coll.AllKeys;

            for (i = 0; i < requestItem.Length; i++)
            {
                sArray.Add(requestItem[i], Request.Form[requestItem[i]]);
            }

            return sArray;
        }

        private Dictionary<string, string> GetQueryStringParams()
        {
            var requestParams = new Dictionary<string, string>();
            foreach (var key in Request.QueryString.AllKeys)
            {
                if (string.IsNullOrEmpty(Request.QueryString[key]))
                    continue;
                requestParams.Add(key.ToLower(), Request.QueryString[key]);
            }
            return requestParams;
        }
        private DbContext Context
        {
            get
            {
                return ServiceLocator.Current.Resolve<DbContext>();
            }
        }
    }
}