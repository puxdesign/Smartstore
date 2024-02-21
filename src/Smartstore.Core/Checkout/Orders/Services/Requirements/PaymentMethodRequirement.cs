﻿using Microsoft.AspNetCore.Http;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Common;

namespace Smartstore.Core.Checkout.Orders.Requirements
{
    public class PaymentMethodRequirement : CheckoutRequirementBase
    {
        private static readonly PaymentMethodType[] _paymentTypes =
        [
            PaymentMethodType.Standard,
            PaymentMethodType.Redirection,
            PaymentMethodType.StandardAndRedirection,
            PaymentMethodType.StandardAndButton
        ];

        private bool? _active;
        private readonly IPaymentService _paymentService;
        private readonly IOrderCalculationService _orderCalculationService;
        private readonly ICheckoutStateAccessor _checkoutStateAccessor;
        private readonly PaymentSettings _paymentSettings;

        public PaymentMethodRequirement(
            IPaymentService paymentService,
            IOrderCalculationService orderCalculationService,
            ICheckoutStateAccessor checkoutStateAccessor,
            IHttpContextAccessor httpContextAccessor,
            PaymentSettings paymentSettings)
            : base(httpContextAccessor)
        {
            _paymentService = paymentService;
            _orderCalculationService = orderCalculationService;
            _checkoutStateAccessor = checkoutStateAccessor;
            _paymentSettings = paymentSettings;
        }

        protected override string ActionName => "PaymentMethod";

        public override int Order => 40;

        public override bool Active => _active ?? true;

        public override async Task<CheckoutRequirementResult> CheckAsync(ShoppingCart cart, object model = null)
        {
            var state = _checkoutStateAccessor.CheckoutState;
            var attributes = cart.Customer.GenericAttributes;

            if (model != null 
                && model is string paymentMethod 
                && IsSameRoute(HttpMethods.Post, ActionName))
            {
                var provider = await _paymentService.LoadPaymentProviderBySystemNameAsync(paymentMethod, true, cart.StoreId);
                if (provider == null)
                {
                    return new(false);
                }

                attributes.SelectedPaymentMethod = paymentMethod;
                await attributes.SaveChangesAsync();

                var form = HttpContext.Request.Form;
                if (form != null)
                {
                    // Save payment data so that the user must not re-enter it.
                    foreach (var pair in form)
                    {
                        var v = pair.Value;
                        state.PaymentData[pair.Key] = v.Count == 2 && v[0] != null && v[0] == "true"
                            ? "true"
                            : v.ToString();
                    }
                }

                // Validate payment data.
                var validationResult = await provider.Value.ValidatePaymentDataAsync(form);
                if (validationResult.IsValid)
                {
                    var paymentInfo = await provider.Value.GetPaymentInfoAsync(form);
                    HttpContext.Session.TrySetObject(CheckoutState.OrderPaymentInfoName, paymentInfo);
                    state.PaymentSummary = await provider.Value.GetPaymentSummaryAsync();

                    return new(true);
                }
                else
                {
                    var errors = validationResult.Errors
                        .Select(x => new CheckoutWorkflowError(x.PropertyName, x.ErrorMessage))
                        .ToArray();

                    return new(false, errors);
                }
            }

            if (_active == null)
            {
                var cartTotal = (Money?)await _orderCalculationService.GetShoppingCartTotalAsync(cart, false);
                state.IsPaymentRequired = cartTotal.GetValueOrDefault() != decimal.Zero;

                if (_paymentSettings.BypassPaymentMethodSelectionIfOnlyOne)
                {
                    var providers = await _paymentService.LoadActivePaymentProvidersAsync(cart, cart.StoreId, _paymentTypes);
                    if (cart.ContainsRecurringItem())
                    {
                        providers = providers.Where(x => x.Value.RecurringPaymentType > RecurringPaymentType.NotSupported);
                    }
                    var paymentMethods = providers.ToList();

                    state.IsPaymentSelectionSkipped = paymentMethods.Count == 1 && !paymentMethods[0].Value.RequiresInteraction;

                    if (state.IsPaymentSelectionSkipped)
                    {
                        attributes.SelectedPaymentMethod = paymentMethods[0].Metadata.SystemName;
                        await attributes.SaveChangesAsync();
                    }
                }

                if (!state.IsPaymentRequired)
                {
                    state.IsPaymentSelectionSkipped = true;
                }

                _active = !state.IsPaymentSelectionSkipped;
            }

            return new(attributes.SelectedPaymentMethod.HasValue());
        }
    }
}
