angular.module('stats').controller('ChatAccountsController', ["$scope", "$http", "$interval", "$timeout", "$rootScope", function ($scope, $http, $interval, $timeout, $rootScope) {
    $scope.items = [];
    $scope.phone = "";
    $scope.isPhoneValid = false;
    $scope.loginCode = "";
    $scope.loginTimerText = "05:00";
    $scope.qrCodeUrl = null;
    $scope.qrLoading = false;
    $scope.currentAdsPowerId = null;
    $scope.isLoginCodeValid = false;

    var loginTimerIntervalPromise = null;
    var loginTimerTimeoutPromise = null;
    var loginTimerDomIntervalId = null;
    var qrPollIntervalPromise = null;

    function clearLoginTimer() {
        if (loginTimerIntervalPromise) {
            $interval.cancel(loginTimerIntervalPromise);
            loginTimerIntervalPromise = null;
        }
    }
    if (loginTimerDomIntervalId) {
        clearInterval(loginTimerDomIntervalId);
        loginTimerDomIntervalId = null;
    }

    $scope.onLoginCodeChange = function () {
        var s = ($scope.loginCode || '').toString();
        s = s.replace(/\D/g, '').substring(0, 6);
        $scope.loginCode = s;
        $scope.isLoginCodeValid = s.length >= 1 && s.length <= 6;
    };

    $scope.addWhatsapp = function () {
        if (window.UserVars && !window.UserVars.IsPaid && window.$) {
            $("#modal-no-subscription").modal();
            return;
        }
        $scope.onPhoneChange();
        if (!$scope.isPhoneValid) {
            return;
        }

        if (window.$) {
            $("#modal-wait").modal('show');
        }

        var payload = {
            Platform: 'Whatsapp',
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
                // Не показываем modal-wait повторно, сразу вызываем loginRequired
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
                return;
            }
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось добавить аккаунт.';
            if (window.toastr && toastr.error) {
                toastr.error(msg);
            } else {
                alert(msg);
            }
        });
    };

    $scope.addMax = function () {
        if (window.UserVars && !window.UserVars.IsPaid && window.$) {
            $("#modal-no-subscription").modal();
            return;
        }
        $scope.onPhoneChange();
        if (!$scope.isPhoneValid) {
            return;
        }

        if (window.$) {
            $("#modal-wait").modal('show');
        }

        var payload = {
            Platform: 'Max',
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
                // Не показываем modal-wait повторно, сразу вызываем loginRequired
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
                return;
            }
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось добавить аккаунт.';
            if (window.toastr && toastr.error) {
                toastr.error(msg);
            } else {
                alert(msg);
            }
        });
    };

    function clearQrPolling() {
        if (qrPollIntervalPromise) {
            $interval.cancel(qrPollIntervalPromise);
            qrPollIntervalPromise = null;
        }
        $scope.qrLoading = false;
    }

    function pollQrCode() {
        if (!$scope.currentAdsPowerId) return;
        $scope.qrLoading = true;
        var url = '/api/QrCode?adsPowerId=' + encodeURIComponent($scope.currentAdsPowerId) + '&_=' + Date.now();
        $http.get(url, { responseType: 'arraybuffer' }).then(function (r) {
            if (r && r.status === 200 && r.data) {
                var blob = new Blob([r.data], { type: 'image/png' });
                var objectUrl = (window.URL || window.webkitURL).createObjectURL(blob);
                $scope.qrCodeUrl = objectUrl;
                clearQrPolling();
            }
        }, function () {
            // 404 is expected until screenshot appears
        });
    }

    function startLoginTimer(secondsTotal) {
        clearLoginTimer();
        var endAt = Date.now() + (secondsTotal * 1000);

        function formatMmSs(left) {
            var m = Math.floor(left / 60);
            var s = left % 60;
            return (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
        }

        function setTimerText(text) {
            $scope.loginTimerText = text;
            try {
                var el = document.getElementById('login-timer-text');
                if (el) el.textContent = text;
            } catch (e) {
            }
        }

        function tick() {
            var leftMs = endAt - Date.now();
            var left = Math.max(0, Math.ceil(leftMs / 1000));
            var text = formatMmSs(left);
            setTimerText(text);
            if (left <= 0) {
                clearLoginTimer();
                if (window.$) {
                    $("#modal-login-required").modal('hide');
                }
            }
        }

        tick();
        // Primary timer: direct DOM updates (independent from Angular digest)
        loginTimerDomIntervalId = setInterval(tick, 1000);
        // Secondary: keep Angular timers to ensure cleanup even if page loses focus
        loginTimerTimeoutPromise = $timeout(function () {
            clearLoginTimer();
            if (window.$) {
                $("#modal-login-required").modal('hide');
            }
        }, secondsTotal * 1000);
    }

    // cleanup when modal is closed by user
    if (window.$) {
        $(document).on('shown.bs.modal', '#modal-login-required', function () {
            // Ensure digest is running for bindings inside the modal
            $rootScope.$applyAsync();
        });
        $(document).on('hidden.bs.modal', '#modal-login-required', function () {
            clearLoginTimer();
            clearQrPolling();
            $scope.qrCodeUrl = null;
            $scope.currentAdsPowerId = null;
            try {
                // If something left a modal backdrop behind, remove it to avoid grey overlay.
                $('.modal-backdrop').remove();
                $('body').removeClass('modal-open');
            } catch (e) {
            }
            $scope.$applyAsync();
        });

        $(document).on('hidden.bs.modal', '#modal-wait', function () {
            try {
                // Ensure all backdrops are removed
                var backdrops = $('.modal-backdrop');
                if (backdrops.length > 0) {
                    backdrops.remove();
                }
                // Check if any modals are open, otherwise remove modal-open class
                if ($('.modal:visible').length === 0) {
                    $('body').removeClass('modal-open');
                }
            } catch (e) {
            }
        });
    }


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
        return $http.get('/api/ChatAccounts').then(function (r) {
            $scope.items = r.data || [];
            return $scope.items;
        }, function (err) {
            if (err && err.status === 401) {
                if (typeof $scope.checkAuth === 'function') {
                    $scope.checkAuth();
                } else if (window.UserVars && !window.UserVars.IsAdmin && window.$) {
                    $("#modal-log-file").modal();
                }
            }
            $scope.items = [];
            return $scope.items;
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

    $scope.loginModalDismissed = false;

    $scope._reloading = false;
    $scope._submittingCode = false;
    $scope.reload = function () {
        if ($scope._reloading) return;
        $scope._reloading = true;
        $scope.load().finally(function () {
            $scope._reloading = false;
        });
    };

    $scope.loginRequired = function (item) {
        if (!item) return;
        // Prevent double clicks and show spinner similar to delete
        if (item._logining) return;
        item._logining = true;
        $scope.loginModalDismissed = false;
        var payload = {
            Platform: item.Platform || item.platform,
            Phone: item.Phone || item.phone
        };
        // remember platform/phone for SubmitCode
        $scope.currentPlatform = payload.Platform;
        $scope.currentPhone = payload.Phone;
        // Show login modal immediately to indicate waiting for code/profile
        $scope.loginCode = "";
        $scope.qrCodeUrl = null;
        $scope.currentAdsPowerId = null;
        $scope.qrLoading = true;
        if (window.$) {
            try { $(".modal").modal('hide'); $('.modal-backdrop').remove(); $('body').removeClass('modal-open'); } catch (e) {}
            var $login = $("#modal-login-required");
            $timeout(function () {
                $login.modal('show');
                $login.off('hidden.bs.modal.reopen');
                $login.on('hidden.bs.modal', function () { $scope.loginModalDismissed = true; });
            }, 50);
        }

        $http.post('/api/ChatAccounts/StartLogin', payload).then(function (r) {
            $scope.qrLoading = false;
            $scope.loginCode = "";
            $scope.qrCodeUrl = null;
            $scope.currentAdsPowerId = (r && r.data && r.data.adsPowerId) ? r.data.adsPowerId : null;
            if (window.$) {
                var $login = $("#modal-login-required");
                $login.one('shown.bs.modal', function () {
                    startLoginTimer(5 * 60);
                    clearQrPolling();
                    pollQrCode();
                    if (qrPollIntervalPromise) { $interval.cancel(qrPollIntervalPromise); }
                    qrPollIntervalPromise = $interval(pollQrCode, 15000);
                });
            } else {
                alert('Вам скоро придёт код. Пожалуйста, введите его для залогинивания аккаунта.');
            }
        }, function (err) {
            // Если дубль (409), просто показываем окно ожидания кода, не показываем ошибку
            if (err && err.status === 409) {
                    if (window.$) {
                        var $login = $("#modal-login-required");
                        try { $('.modal-backdrop').remove(); $('body').removeClass('modal-open'); } catch (e) {}
                        $timeout(function () {
                            $login.modal('show');
                            $login.one('shown.bs.modal', function () {
                                startLoginTimer(5 * 60);
                                clearQrPolling();
                                pollQrCode();
                                if (qrPollIntervalPromise) { $interval.cancel(qrPollIntervalPromise); }
                                qrPollIntervalPromise = $interval(pollQrCode, 15000);
                            });
                            $login.off('hidden.bs.modal.reopen');
                            $login.on('hidden.bs.modal', function () {
                                $scope.loginModalDismissed = true;
                            });
                        }, 50);
                    } else {
                    alert('Вам скоро придёт код. Пожалуйста, введите его для залогинивания аккаунта.');
                }
                return;
            }
            if (window.$) {
                $("#modal-login-required").modal('hide');
            }
            var msg = 'Не удалось запустить профиль для залогинивания.';
            // Handle network errors
            if (err && (err.status === 0 || err.status === -1)) {
                msg = 'Ошибка соединения с сервером. Проверьте подключение к интернету и попробуйте снова.';
            } else if (err && err.status === 402) {
                msg = 'Нет активной подписки.';
            } else if (err && err.status === 401) {
                msg = 'Сессия истекла. Пожалуйста, перезагрузите страницу.';
            } else if (err && err.data && (err.data.Message || err.data.message)) {
                msg = err.data.Message || err.data.message;
            }
            if (window.toastr && toastr.error) {
                toastr.error(msg);
            } else {
                alert(msg);
            }
            if (window.$) {
                $("#modal-login-required").modal('hide');
                try {
                    $('.modal-backdrop').remove();
                    $('body').removeClass('modal-open');
                } catch (e) {
                }
            }
        }).finally(function () {
            if (window.$) {
                $("#modal-wait").modal('hide');
                // restore button state
                try { item._logining = false; } catch (e) { }
            }
        });
    };

    $scope.loadChats = function (item) {
        if (!item) return;
        if (item._loadingChats) return;
        item._loadingChats = true;

        var payload = {
            Platform: item.Platform || item.platform,
            Phone: item.Phone || item.phone
        };

        $http.post('/api/ChatAccounts/StartLoadChats', payload).then(function (r) {
            // server will enqueue rpa_task and wait up to 5 minutes; when returns OK we refresh list
            if (r && r.status === 202) {
                var warn = 'Задача на загрузку чатов поставлена в очередь, но не была обработана в отведённое время. Проверьте позже.';
                if (window.toastr && toastr.warning) toastr.warning(warn); else alert(warn);
            }
            $scope.load().finally(function () { item._loadingChats = false; });
        }, function (err) {
            var msg = 'Не удалось запустить загрузку чатов.';
            if (err && (err.status === 0 || err.status === -1)) msg = 'Ошибка соединения с сервером.';
            else if (err && err.status === 402) msg = 'Нет активной подписки.';
            else if (err && err.status === 409) msg = 'Задача загрузки чатов уже находится в очереди.';
            else if (err && err.data && (err.data.Message || err.data.message)) msg = err.data.Message || err.data.message;
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
            item._loadingChats = false;
        });
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
        if (item._deleting) return;
        item._deleting = true;
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
        }).finally(function () {
            item._deleting = false;
        });
    };

    $scope.submitLoginCode = function () {
        if (!$scope.isLoginCodeValid || !$scope.currentAdsPowerId) {
            return;
        }

        var payload = {
            Platform: $scope.currentPlatform,
            Phone: $scope.currentPhone,
            Code: $scope.loginCode
        };

        if ($scope._submittingCode) return;
        $scope._submittingCode = true;

        $http.post('/api/ChatAccounts/SubmitCode', payload).then(function (r) {
            clearLoginTimer();
            $scope.loginCode = "";
            $scope.qrCodeUrl = null;
            $scope.currentAdsPowerId = null;
            if (window.$) {
                $("#modal-login-required").modal('hide');
            }
            $scope.load();
            if (window.toastr && toastr.success) {
                toastr.success('Аккаунт успешно залогинирован!');
            }
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Неверный код. Попробуйте снова.';
            if (err && err.status === 409) msg = 'Код уже обрабатывается.';
            if (err && err.status === 202) msg = 'Код поставлен в очередь и обрабатывается асинхронно.';
            if (window.toastr && toastr.error) {
                toastr.error(msg);
            } else {
                alert(msg);
            }
        }).finally(function () {
            $scope._submittingCode = false;
        });
    };

    $scope.load();

    // Show login prompt immediately for unauthenticated users
    try {
        if (window.$) {
            var hasAuthCookie = (document.cookie || '').indexOf('.ASPXAUTH=') >= 0;
            var isAuthed = hasAuthCookie;
            if (window.UserVars) {
                isAuthed = isAuthed || !!window.UserVars.IsAuthenticated;
                isAuthed = isAuthed || (!!window.UserVars.Name && window.UserVars.Name !== '?');
            }

            if (!isAuthed) $("#modal-log-file").modal();
        }
    } catch (e) {
        // ignore
    }
}]);
