angular.module('stats').controller('ChatAccountsController', ["$scope", "$http", function ($scope, $http) {
    $scope.items = [];
    $scope.phone = "";
    $scope.isPhoneValid = false;
    $scope.loginCode = "";

    $scope.normalizePhone = function (value) {
        if (!value) return "";
        var s = ("" + value).trim();
        // keep digits and optional leading plus
        s = s.replace(/[^0-9+]/g, '');
        if (s.indexOf('+') > 0) {
            s = s.replace(/\+/g, '');
        }
        if (s.length > 0 && s[0] !== '+') {
            // ok
        }
        // remove all pluses except first character
        if (s.length > 0) {
            s = s[0] + s.substring(1).replace(/\+/g, '');
        }
        // also trim by max length (E.164 is 15 digits + '+')
        return s.substring(0, 16);
    };

    $scope.validatePhone = function (value) {
        var s = $scope.normalizePhone(value);
        // E.164: '+' optional, 7..15 digits total
        var digits = s.replace(/\D/g, '');
        if (digits.length < 7 || digits.length > 15) return false;
        return /^\+?\d+$/.test(s);
    };

    $scope.onPhoneChange = function () {
        $scope.phone = $scope.normalizePhone($scope.phone);
        $scope.isPhoneValid = $scope.validatePhone($scope.phone);
    };

    $scope.load = function () {
        $http.get('/api/ChatAccounts').then(function (r) {
            $scope.items = r.data || [];
        }, function (err) {
            if (err && err.status === 401) {
                if (typeof $scope.checkAuth === 'function') {
                    $scope.checkAuth();
                } else if (window.UserVars && !window.UserVars.IsAdmin && window.$) {
                    $("#modal-log-file").modal();
                }
            }
            $scope.items = [];
        });
    };

    $scope.getChatCount = function (item) {
        if (!item) return 0;
        var json = item.ChatsJson || item.chats_json;
        if (!json) return 0;
        try {
            var arr = angular.fromJson(json);
            return Array.isArray(arr) ? arr.length : 0;
        } catch (e) {
            return 0;
        }
    };

    $scope.loginRequired = function (item) {
        // Placeholder for the next step (open AdsPower profile / Telegram login flow)
        alert('Требуется залогинивание в аккаунт.');
    };

    $scope.loadChats = function (item) {
        // Placeholder: implement server endpoint to fetch chats and update item.ChatsJson
        alert('Загрузка чатов пока не реализована.');
    };

    $scope.addTelegram = function () {
        if (window.UserVars && !window.UserVars.IsPaid && window.$) {
            $("#modal-no-subscription").modal();
            return;
        }
        $scope.onPhoneChange();
        if (!$scope.isPhoneValid) {
            return;
        }

        if (window.$) {
            $("#modal-wait").modal();
        }

        var payload = {
            Platform: 'Telegram',
            Phone: $scope.phone,
            Status: 0
        };

        $http.post('/api/ChatAccounts', payload).then(function () {
            $scope.phone = "";
            $scope.isPhoneValid = false;
            $scope.load().then(function () {
                if (window.$) {
                    $("#modal-wait").modal('hide');
                }

                var platform = payload.Platform || payload.platform;
                var phone = payload.Phone || payload.phone;
                var added = null;
                if ($scope.items && $scope.items.length) {
                    for (var i = 0; i < $scope.items.length; i++) {
                        var it = $scope.items[i];
                        var p = it.Platform || it.platform;
                        var ph = it.Phone || it.phone;
                        if (p === platform && ph === phone) {
                            added = it;
                            break;
                        }
                    }
                }

                // Auto-trigger "login required" flow after successful add
                $scope.loginRequired(added || payload);
            });
        }, function (err) {
            if (window.$) {
                $("#modal-wait").modal('hide');
            }
            if (err && err.status === 409) {
                var msg = (err.data && (err.data.Message || err.data.message)) || 'Указанный номер телефона уже в базе';
                if (window.toastr && toastr.error) {
                    toastr.error(msg);
                } else {
                    alert(msg);
                }
                return;
            }
            if (err && err.status === 402 && window.$) {
                $("#modal-no-subscription").modal();
                return;
            }
            if (err && err.status === 401) {
                if (typeof $scope.checkAuth === 'function') {
                    $scope.checkAuth();
                } else if (window.UserVars && !window.UserVars.IsAdmin && window.$) {
                    $("#modal-log-file").modal();
                }
            }
        });
    };

    $scope.remove = function (item) {
        if (!item) return;
        var url = '/api/ChatAccounts?platform=' + encodeURIComponent(item.Platform || item.platform || '') + '&phone=' + encodeURIComponent(item.Phone || item.phone || '');
        $http.delete(url).then(function () {
            $scope.load();
        }, function (err) {
            if (err && err.status === 401) {
                if (typeof $scope.checkAuth === 'function') {
                    $scope.checkAuth();
                } else if (window.UserVars && !window.UserVars.IsAdmin && window.$) {
                    $("#modal-log-file").modal();
                }
            }
        });
    };

    $scope.load();
}]);
