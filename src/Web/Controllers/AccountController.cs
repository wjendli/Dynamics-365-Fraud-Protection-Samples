// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Contoso.FraudProtection.ApplicationCore.Interfaces;
using Contoso.FraudProtection.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Contoso.FraudProtection.Web.ViewModels.Account;
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Dynamics.FraudProtection.Models;
using Microsoft.Dynamics.FraudProtection.Models.SignupEvent;
using Microsoft.Dynamics.FraudProtection.Models.SharedEntities;

namespace Contoso.FraudProtection.Web.Controllers
{
    [Route("[controller]/[action]")]
    [Authorize]
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IBasketService _basketService;
        private readonly IFraudProtectionService _fraudProtectionService;
        private readonly IHttpContextAccessor _contextAccessor;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IBasketService basketService,
            IFraudProtectionService fraudProtectionService,
            IHttpContextAccessor contextAccessor)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _basketService = basketService;
            _fraudProtectionService = fraudProtectionService;
            _contextAccessor = contextAccessor;
        }

        // GET: /Account/SignIn 
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> SignIn(string returnUrl = null)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            ViewData["ReturnUrl"] = returnUrl;
            if (!String.IsNullOrEmpty(returnUrl) &&
                returnUrl.IndexOf("checkout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ViewData["ReturnUrl"] = "/Basket/Index";
            }

            return View();
        }

        // POST: /Account/SignIn
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SignIn(LoginViewModel model, string returnUrl = null)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            ViewData["ReturnUrl"] = returnUrl;

            var result = await _signInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);
            if (result.Succeeded)
            {
                await TransferBasketToEmailAsync(model.Email);
                return RedirectToLocal(returnUrl);
            }
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Lockout()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> SignOut()
        {
            await _signInManager.SignOutAsync();

            return RedirectToAction(nameof(CatalogController.Index), "Catalog");
        }

        [AllowAnonymous]
        public IActionResult Register()
        {
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model, string returnUrl = null)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            if (_contextAccessor.HttpContext.Connection == null)
                throw new Exception(nameof(_contextAccessor.HttpContext.Connection));

            #region Fraud Protection Service
            // Ask Fraud Protection to assess this signup/registration before registering the user in our database, etc.
            var signupAddress = new SignupAddress
            {
                FirstName = model.FirstName,
                LastName = model.LastName,
                PhoneNumber = model.Phone,
                SignupAddressDetails = new AddressDetails
                {
                    Street1 = model.Address1,
                    Street2 = model.Address2,
                    City = model.City,
                    State = model.State,
                    ZipCode = model.ZipCode,
                    Country = model.CountryRegion
                }
            };

            var signupUser = new User<SignupUserDetails>
            {
                UserId = model.Email,
                UserDetails = new SignupUserDetails
                {
                    CreationDate = DateTimeOffset.Now,
                    UpdateDate = DateTimeOffset.Now,
                    FirstName = model.FirstName,
                    LastName = model.LastName,
                    Country = model.CountryRegion,
                    ZipCode = model.ZipCode,
                    TimeZone = new TimeSpan(0, 0, -model.ClientTimeZone, 0).ToString(),
                    Language = "EN-US",
                    PhoneNumber = model.Phone,
                    Email = model.Email,
                    ProfileType = UserProfileType.Consumer.ToString(),
                    SignUpAddress = signupAddress
                }
            };

            var deviceContext = new DeviceContext
            {
                DeviceContextId = _contextAccessor.GetSessionId(),
                IPAddress = _contextAccessor.HttpContext.Connection.RemoteIpAddress.ToString(),
                DeviceContextDetails = new DeviceContextDetails
                {
                    DeviceContextDC = model.FingerPrintingDC,
                    Provider = DeviceContextProvider.DFPFINGERPRINTING.ToString(),
                }
            };

            var marketingContext = new MarketingContext
            {
                Type = MarketingType.Direct.ToString(),
                IncentiveType = MarketingIncentiveType.None.ToString(),
                IncentiveOffer = "Integrate with Fraud Protection"
            };

            var storefrontContext = new StoreFrontContext
            {
                StoreName = "Fraud Protection Sample Site",
                Type = StorefrontType.Web.ToString(),
                Market = "US"
            };

            var signupEvent = new SignUp
            {
                SignUpId = Guid.NewGuid().ToString(),
                AssessmentType = AssessmentType.protect.ToString(),
                User = signupUser,
                MerchantLocalDate = DateTimeOffset.Now,
                CustomerLocalDate = model.ClientDate,
                MarketingContext = marketingContext,
                StoreFrontContext = storefrontContext,
                DeviceContext = deviceContext,
            };

            var signupAssessment = await _fraudProtectionService.PostSignup(signupEvent);

            //TODO - show response
            //TODO - Configurable or something?  Or, flip a coin in code since a user would lose much (and can try again right away?)
            if (signupAssessment.ResultDetails.RiskScore > 20)
            {
                ModelState.AddModelError("", "Signup rejected by Fraud Protection");
                return View(model);
            }

            //TODO consider response and send signup status either way.
            #endregion

            //Only create the user in the sample site if the Fraud Protection merchant decision is APPROVE
            var user = new ApplicationUser
            {
                UserName = model.Email,
                Email = model.Email,
                FirstName = model.FirstName,
                LastName = model.LastName,
                PhoneNumber = model.Phone,
                Address1 = model.Address1,
                Address2 = model.Address2,
                City = model.City,
                State = model.State,
                ZipCode = model.ZipCode,
                CountryRegion = model.CountryRegion
            };

            var result = await _userManager.CreateAsync(user, model.Password);
            if (!result.Succeeded)
            {
                AddErrors(result);
                return View(model);
            }

            if (user == null)
                throw new Exception(nameof(user));

            await _signInManager.SignInAsync(user, isPersistent: false);

            await TransferBasketToEmailAsync(user.Email);

            return RedirectToLocal(returnUrl);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ConfirmEmail(string userId, string code)
        {
            if (userId == null || code == null)
            {
                return RedirectToAction(nameof(CatalogController.Index), "Catalog");
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                throw new ApplicationException($"Unable to load user with ID '{userId}'.");
            }
            var result = await _userManager.ConfirmEmailAsync(user, code);
            return View(result.Succeeded ? "ConfirmEmail" : "Error");
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPassword(string code = null)
        {
            if (code == null)
            {
                throw new ApplicationException("A code must be supplied for password reset.");
            }
            var model = new ResetPasswordViewModel { Code = code };
            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult AccessDenied(string returnUrl)
        {
            return View();
        }

        private IActionResult RedirectToLocal(string returnUrl)
        {
            if (Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            else
            {
                return RedirectToAction(nameof(CatalogController.Index), "Catalog");
            }
        }

        private void AddErrors(IdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }
        }

        private async Task TransferBasketToEmailAsync(string email)
        {
            string anonymousBasketId = Request.Cookies[Constants.BASKET_COOKIENAME];
            if (!string.IsNullOrEmpty(anonymousBasketId))
            {
                await _basketService.TransferBasketAsync(anonymousBasketId, email);
                Response.Cookies.Delete(Constants.BASKET_COOKIENAME);
            }
        }
    }
}
