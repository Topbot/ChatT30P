angular.module('stats').controller('MessagesController', ["$scope", "$http", "$location", function ($scope, $http, $location) {
    $scope.isPaid = !!(window.UserVars && window.UserVars.IsPaid);
    $scope.items = [];
    $scope.searchText = "";
    $scope.sortField = "DateTicks";
    $scope.sortReverse = true;
    $scope._loading = false;
    $scope.totalMessages = 0;
    $scope.periodStart = null;
    $scope.periodEnd = null;
    $scope.chatTitle = "";
    $scope.chatUsername = "";
    $scope.pageSizes = [50, 100, 200, 300];
    $scope.pageSize = 50;
    $scope.currentPage = 1;

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

    $scope.loadTitle = function () {
        var chatId = $location.search().chatId;
        if (!chatId) {
            $scope.chatTitle = "";
            $scope.chatUsername = "";
            return;
        }
        $http.get('/api/Chats/Title?chatId=' + encodeURIComponent(chatId)).then(function (r) {
            $scope.chatTitle = (r && r.data && r.data.Title) ? r.data.Title : "";
            $scope.chatUsername = (r && r.data && r.data.Username) ? r.data.Username : "";
        }, function () {
            $scope.chatTitle = "";
            $scope.chatUsername = "";
        });
    };

    $scope.exportExcel = function () {
        var chatId = $location.search().chatId;
        if (!chatId) return;
        var url = '/api/Chats/MessagesExport?chatId=' + encodeURIComponent(chatId);
        var qs = [];
        if ($scope.periodStart) qs.push('start=' + encodeURIComponent(formatDate($scope.periodStart)));
        if ($scope.periodEnd) qs.push('end=' + encodeURIComponent(formatDate($scope.periodEnd)));
        if (qs.length > 0) url += '&' + qs.join('&');
        window.location.href = url;
    };

    $scope.copyMessage = function (item) {
        if (!item || !item.Text) return;
        var text = item.Text;
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text);
            return;
        }
        var textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.left = '-9999px';
        document.body.appendChild(textarea);
        textarea.select();
        try { document.execCommand('copy'); } catch (e) { }
        document.body.removeChild(textarea);
    };

    $scope.openMessage = function (item) {
        var chatId = $location.search().chatId;
        if (!item || !chatId) return;
        var msgId = item.MessageId || item.messageId;
        if (!msgId) return;
        var url = null;
        if ($scope.chatUsername) {
            if (chatId.indexOf('channel:') === 0) {
                var id = chatId.substring('channel:'.length);
                var parts = id.split(':');
                var channelId = parts[0];
                url = 'https://t.me/' + $scope.chatUsername + '/' + channelId + '/' + msgId;
            } else {
                url = 'https://t.me/' + $scope.chatUsername + '/' + msgId;
            }
        } else if (chatId.indexOf('channel:') === 0) {
            var id = chatId.substring('channel:'.length);
            var parts = id.split(':');
            var channelId = parts[0];
            url = 'https://t.me/c/' + channelId + '/' + msgId;
        } else if (chatId.indexOf('chat:') === 0) {
            var cid = chatId.substring('chat:'.length);
            url = 'https://t.me/c/' + cid + '/' + msgId;
        }
        if (url) {
            window.open(url, '_blank');
        }
    };

    $scope.setQuickRange = function (range) {
        var now = new Date();
        var start = new Date(now.getTime());
        if (range === 'today') {
            start.setHours(0, 0, 0, 0);
        } else if (range === 'week') {
            start.setDate(start.getDate() - 7);
        } else if (range === '2months') {
            start.setMonth(start.getMonth() - 2);
        } else {
            start.setMonth(start.getMonth() - 1);
        }
        $scope.periodStart = start;
        $scope.periodEnd = now;
        $scope.refresh();
    };

    $scope.messageFilter = function (item) {
        if (!$scope.searchText) return true;
        var text = ($scope.searchText || '').toLowerCase();
        var message = (item && item.Text ? item.Text : '').toLowerCase();
        var tokens = text.split(/\u007F|\s+/).filter(function (t) { return t.length > 0; });
        if (tokens.length === 0) return true;
        for (var i = 0; i < tokens.length; i++) {
            if (message.indexOf(tokens[i]) < 0) return false;
        }
        return true;
    };

    $scope.setSentiment = function (item, sentiment) {
        if (!item || !item.MessageId || !sentiment) return;
        var chatId = $location.search().chatId;
        if (!chatId) return;
        item.Sentiment = sentiment;
        var payload = { ChatId: chatId, MessageId: item.MessageId, Sentiment: sentiment };
        $http.put('/api/Chats/MessagesSentiment', payload).then(function () {
        }, function () {
        });
    };

    $scope.sentimentLabel = function (item) {
        if (!item || !item.Sentiment) return '';
        switch (item.Sentiment) {
            case 'positive': return 'Позитивное';
            case 'negative': return 'Негативное';
            case 'neutral': return 'Нейтральное';
            default: return item.Sentiment;
        }
    };

    $scope.getSentimentClass = function (item) {
        if (!item || !item.Sentiment) return '';
        if (item.Sentiment === 'positive') return 'sentiment-positive';
        if (item.Sentiment === 'negative') return 'sentiment-negative';
        return '';
    };

    $scope.truncateMessage = function (text) {
        if (!text) return '';
        var s = '' + text;
        if (s.length <= 1000) return s;
        return s.substring(0, 1000) + '…';
    };

    $scope.relativeDate = function (item) {
        if (!item || !item.DateText) return '';
        try {
            if (window.moment) {
                return moment(item.DateText, 'YYYY-MM-DD HH:mm:ss').fromNow();
            }
        } catch (e) {
        }
        return '';
    };
    $scope.refresh = function () {
        if ($scope._loading) return;
        var chatId = $location.search().chatId;
        if (!chatId) {
            $scope.items = [];
            return;
        }
        $scope._loading = true;
        var url = '/api/Chats/Messages?chatId=' + encodeURIComponent(chatId);
        var qs = [];
        if ($scope.periodStart) qs.push('start=' + encodeURIComponent(formatDate($scope.periodStart)));
        if ($scope.periodEnd) qs.push('end=' + encodeURIComponent(formatDate($scope.periodEnd)));
        if (qs.length > 0) url += '&' + qs.join('&');
        $http.get(url).then(function (r) {
            $scope.items = r.data || [];
            $scope.totalMessages = $scope.items.length;
            $scope.currentPage = 1;
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось загрузить сообщения.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            $scope._loading = false;
        });
    };

    $scope.pageCount = function () {
        var total = ($scope.filteredItems && $scope.filteredItems.length) ? $scope.filteredItems.length : ($scope.items ? $scope.items.length : 0);
        var size = $scope.pageSize || 50;
        return Math.max(1, Math.ceil(total / size));
    };

    $scope.setPage = function (page) {
        var count = $scope.pageCount();
        var next = Math.max(1, Math.min(page, count));
        $scope.currentPage = next;
    };

    $scope.nextPage = function () {
        if ($scope.currentPage < $scope.pageCount()) {
            $scope.currentPage++;
        }
    };

    $scope.prevPage = function () {
        if ($scope.currentPage > 1) {
            $scope.currentPage--;
        }
    };

    $scope.visiblePages = function () {
        var total = $scope.pageCount();
        var current = $scope.currentPage;
        var pages = [];
        var start = Math.max(1, current - 2);
        var end = Math.min(total, start + 4);
        if (end - start < 4) {
            start = Math.max(1, end - 4);
        }
        for (var i = start; i <= end; i++) {
            pages.push(i);
        }
        return pages;
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

    if ($scope.isPaid) {
        var now = new Date();
        var start = new Date(now.getTime());
        start.setMonth(start.getMonth() - 1);
        $scope.periodStart = start;
        $scope.periodEnd = now;
        $scope.loadTitle();
        $scope.refresh();
    }
}]);
