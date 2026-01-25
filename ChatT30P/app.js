function new_window(url) {
    width_screen = (screen.width - 700) / 2;
    height_screen = (screen.height - 400) / 2;
    params = 'menubar=0, toolbar=0, location=0, directories=0, status=0, scrollbars=0, resizable=0, width=700, height=400, left=' + width_screen + ', top=' + height_screen;
    window.open(url, 'newwin', params);
}

function goPay() {
    alert('Для приобретения платного аккаунта напишите письмо о своем желании на topbot@yandex.ru');
}

(function () {
    var app = angular.module("stats", ['ngRoute', 'ngAnimate', 'ngSanitize', 'ui-rangeSlider','angularjs-dropdown-multiselect']);

    var config = ["$routeProvider", function ($routeProvider) {
        $routeProvider
            .when("/", { templateUrl: "views/chataccounts.html?v=1" })
        .otherwise({ redirectTo: "/" });
    }];
    app.config(config);

    app.directive('focusMe', ['$timeout', function ($timeout) {
        return function (scope, element, attrs) {
            scope.$watch(attrs.focusMe, function (value) {
                if (value) {
                    $timeout(function () {
                        element.focus();
                    }, 700);
                }
            });
        };
    }]);

    app.run(["$rootScope", "dataService", function ($rootScope, dataService) {
        $rootScope.checkAuth = dataService.checkAuth;
    }]);

    var run = ["$rootScope", "$log", function ($rootScope, $log) {
        $rootScope.lbl = BlogAdmin.i18n;
        $rootScope.SiteVars = SiteVars;
        toastr.options.positionClass = 'toast-bottom-right';
        toastr.options.backgroundpositionClass = 'toast-bottom-right';
        toastr.options.preventDuplicates = true;
        toastr.options.timeOut = 15000;
        (function () {
            var po = document.createElement('script');
            po.type = 'text/javascript';
            po.async = true;
            po.charset = 'windows-1251';
            po.src = '//vk.com/js/api/share.js?11';
            var s = document.getElementsByTagName('script')[0];
            s.parentNode.insertBefore(po, s);
        })();
        setTimeout(function () {
            try {
                document.getElementById('vk-button').innerHTML = VK.Share.button({ url: 'https://chat.t30p.ru' }, { type: 'button', text: 'Поделиться', title: 'Мониторинг приватных чатов Телеграм' });
            } catch (e) {

            }
        }, 10000);
    }];

    app.run(run);

    angular.module('stats').factory("dataService", ["$http", "$q", function ($http, $q) {
        return {
            checkAuth: function() {
                if (!UserVars.IsAdmin) {
                    $("#modal-log-file").modal();
                }
                return UserVars.IsAdmin;
            },
            getItems: function (url, p, success, error) {
                    return $http.get(webRoot(url), {
                        // query string like { userId: user.id } -> ?userId=value
                        params: p
                    }).then(success, error);
            },
            addItem: function (url, item, success, error) {
                return $http({
                    url: webRoot(url),
                    method: 'POST',
                    data: item
                }).then(success, error);
            },
            updateItem: function (url, item, success, error) {
                return $http({
                    url: webRoot(url),
                    method: 'PUT',
                    data: item
                }).then(success, error);
            },
            // pass list to process all in one go
            processChecked: function (url, items, success, error) {
                return $http({
                    url: webRoot(url),
                    method: 'PUT',
                    data: items
                }).then(success, error);
            }
        };
    }]);

    app.filter('socialLinks', function () {
        return function (links) {
            var about = "";
            if (links != null) {
                var arr = links.split('\n');
                for (var i = 0; i < arr.length; i++) {
                    about += "<a href='" + arr[i] + "' target='_blank'>" + arr[i] + "</a><br/>\n";
                }
            }
            return about;
        };
    });

    app.filter('bigNumber', function () {
        return function (number) {
            if (number == 0) {
                return 0;
            }
            else {
                // hundreds
                if (number <= 999) {
                    return number;
                }
                // thousands
                else if (number >= 1000 && number <= 999999) {
                    return (Math.round(number / 1000 * 100) / 100) + 'K';
                }
                // millions
                else if (number >= 1000000 && number <= 999999999) {
                    return (Math.round(number / 1000000 * 100) / 100) + 'M';
                }
                // billions
                else if (number >= 1000000000 && number <= 999999999999) {
                    return (Math.round(number / 1000000000 * 100) / 100) + 'B';
                }
                else {
                    return number;
                }
            }
        };
    });
})();
