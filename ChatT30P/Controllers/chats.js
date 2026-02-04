angular.module('stats').controller('ChatsController', ["$scope", "$http", "$location", function ($scope, $http, $location) {
    $scope.isPaid = !!(window.UserVars && window.UserVars.IsPaid);
    $scope.items = [];
    $scope.searchText = "";
    $scope.sortField = "Title";
    $scope.sortReverse = false;
    $scope._loading = false;
    $scope.totalMessages = 0;
    $scope.periodStart = null;
    $scope.periodEnd = null;
    $scope.activeRange = 'month';

    if (!$scope.isPaid && window.$) {
        $("#modal-no-subscription").modal();
    }

    $scope.setSort = function (field) {
        if ($scope.sortField === field) {
            $scope.sortReverse = !$scope.sortReverse;
        } else {
            $scope.sortField = field;
            $scope.sortReverse = false;
        }
    };

    $scope.getToneTotal = function (item) {
        if (!item) return 0;
        return (item.PositiveCount || 0)
            + (item.NeutralCount || 0)
            + (item.NegativeCount || 0)
            + (item.UnknownCount || 0);
    };

    $scope.getToneWidth = function (item, key) {
        var total = $scope.getToneTotal(item);
        if (total <= 0) return '0%';
        var value = 0;
        switch (key) {
            case 'positive': value = item.PositiveCount || 0; break;
            case 'neutral': value = item.NeutralCount || 0; break;
            case 'negative': value = item.NegativeCount || 0; break;
            case 'unknown': value = item.UnknownCount || 0; break;
        }
        return (value / total * 100).toFixed(1) + '%';
    };

    $scope.setQuickRange = function (range) {
        $scope.activeRange = range;
        var now = new Date();
        var start = new Date(now.getTime());
        if (range === 'today') {
            start.setHours(0, 0, 0, 0);
        } else if (range === 'week') {
            start.setDate(start.getDate() - 7);
            start.setHours(0, 0, 0, 0);
        } else if (range === '2months') {
            start.setMonth(start.getMonth() - 2);
            start.setHours(0, 0, 0, 0);
        } else {
            start.setMonth(start.getMonth() - 1);
            start.setHours(0, 0, 0, 0);
        }
        $scope.periodStart = start;
        $scope.periodEnd = now;
        $scope.refresh();
    };

    $scope.onPeriodChange = function () {
        $scope.activeRange = null;
        $scope.refresh();
    };

    $scope.chatFilter = function (item) {
        if (!$scope.searchText) return true;
        var text = ($scope.searchText || '').toLowerCase();
        var title = (item && item.Title ? item.Title : '').toLowerCase();
        var comment = (item && item.Comment ? item.Comment : '').toLowerCase();
        return title.indexOf(text) >= 0 || comment.indexOf(text) >= 0;
    };

    $scope.openMessages = function (item) {
        if (!item || !item.ChatId) return;
        $location.path('/messages').search({ chatId: item.ChatId });
    };

    $scope.refresh = function () {
        if ($scope._loading) return;
        $scope._loading = true;
        var url = '/api/Chats';
        var qs = [];
        if ($scope.periodStart) qs.push('start=' + encodeURIComponent(formatDate($scope.periodStart)));
        if ($scope.periodEnd) {
            var endDate = new Date($scope.periodEnd);
            endDate.setDate(endDate.getDate() + 1);
            qs.push('end=' + encodeURIComponent(formatDate(endDate)));
        }
        if (qs.length > 0) url += '?' + qs.join('&');
        $http.get(url).then(function (r) {
            $scope.items = r.data || [];
            var total = 0;
            for (var i = 0; i < $scope.items.length; i++) {
                total += $scope.items[i].MessageCount || 0;
            }
            $scope.totalMessages = total;
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось загрузить чаты.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            $scope._loading = false;
        });
    };

    $scope.saveComment = function (item) {
        if (!item || !item.ChatId) return;
        if (item._saving) return;
        item._saving = true;
        var payload = {
            ChatId: item.ChatId,
            Phone: item.Phone,
            Comment: item.Comment
        };
        $http.put('/api/Chats', payload).then(function () {
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось сохранить комментарий.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            item._saving = false;
        });
    };

    function formatDate(value) {
        if (!value) return '';
        var d = value instanceof Date ? value : new Date(value);
        if (isNaN(d.getTime())) return '';
        var year = d.getFullYear();
        var month = ('0' + (d.getMonth() + 1)).slice(-2);
        var day = ('0' + d.getDate()).slice(-2);
        return year + '-' + month + '-' + day;
    }

    $scope.remove = function (item) {
        if (!item || !item.ChatId) return;
        if (!confirm('Удалить чат "' + (item.Title || item.ChatId) + '"?')) return;
        if (item._deleting) return;
        item._deleting = true;
        var url = '/api/Chats?chatId=' + encodeURIComponent(item.ChatId);
        if (item.Phone) url += '&phone=' + encodeURIComponent(item.Phone);
        $http.delete(url).then(function () {
            $scope.refresh();
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось удалить чат.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            item._deleting = false;
        });
    };

    if ($scope.isPaid) {
        var now = new Date();
        var start = new Date(now.getTime());
        start.setMonth(start.getMonth() - 1);
        $scope.periodStart = start;
        $scope.periodEnd = now;
        $scope.activeRange = 'month';
        $scope.refresh();
    }
}]);
