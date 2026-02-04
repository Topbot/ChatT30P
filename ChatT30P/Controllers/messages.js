angular.module('stats').controller('MessagesController', ["$scope", "$http", "$location", function ($scope, $http, $location) {
    $scope.isPaid = !!(window.UserVars && window.UserVars.IsPaid);
    $scope.items = [];
    $scope.filteredItems = [];
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
    $scope.activeRange = '2months';
    $scope.activeTab = 'table';
    $scope.chartType = 'stacked';
    $scope.sentimentTotal = 0;
    $scope.pieChart = { width: 240, height: 240, slices: [], legend: [] };
    $scope.barChart = { width: 0, height: 0, days: [], segments: [], labels: [] };
    $scope.topAllCommenters = [];
    $scope.topPositiveCommenters = [];
    $scope.topNegativeCommenters = [];

    var sentimentMeta = {
        positive: { label: 'Позитивное', color: '#5cb85c' },
        neutral: { label: 'Нейтральное', color: '#337ab7' },
        negative: { label: 'Негативное', color: '#d9534f' },
        unknown: { label: 'O — не определено', color: '#999' }
    };
    var sentimentOrder = ['positive', 'neutral', 'negative', 'unknown'];

    $scope.setTab = function (tab) {
        $scope.activeTab = tab || 'table';
    };

    $scope.setChartType = function (type) {
        $scope.chartType = type || 'stacked';
        updateCharts();
    };

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
        if ($scope.activeTab === 'charts') qs.push('mode=charts');
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
        var topicMarker = ':topic:';
        var normalizedChatId = chatId;
        var topicIndex = chatId.toLowerCase().lastIndexOf(topicMarker);
        if (topicIndex >= 0) normalizedChatId = chatId.substring(0, topicIndex);
        if ($scope.chatUsername) {
            url = 'https://t.me/' + $scope.chatUsername + '/' + msgId;
        } else if (normalizedChatId.indexOf('channel:') === 0) {
            var id = normalizedChatId.substring('channel:'.length);
            var parts = id.split(':');
            var channelId = parts[0];
            var username = parts.length > 1 ? parts.slice(1).join(':') : '';
            url = username ? ('https://t.me/' + username + '/' + msgId) : ('https://t.me/c/' + channelId + '/' + msgId);
        } else if (normalizedChatId.indexOf('chat:') === 0) {
            var cid = normalizedChatId.substring('chat:'.length);
            url = 'https://t.me/c/' + cid + '/' + msgId;
        }
        if (url) {
            window.open(url, '_blank');
        }
    };

    $scope.setQuickRange = function (range) {
        $scope.activeRange = range;
        var now = new Date();
        var start = new Date(now.getTime());
        var end = new Date(now.getTime());
        end.setHours(0, 0, 0, 0);
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
        $scope.periodEnd = end;
        $scope.refresh();
    };

    $scope.onPeriodChange = function () {
        $scope.activeRange = null;
        $scope.refresh();
    };

    function parseMessageDateUtc(item) {
        if (!item || !item.DateText) return null;
        var dt = new Date(item.DateText.replace(' ', 'T') + 'Z');
        return isNaN(dt.getTime()) ? null : dt;
    }

    function getRangeBounds() {
        var start = $scope.periodStart ? new Date($scope.periodStart) : null;
        var end = $scope.periodEnd ? new Date($scope.periodEnd) : null;
        if (start) start.setHours(0, 0, 0, 0);
        if (end) {
            end.setHours(0, 0, 0, 0);
            end.setDate(end.getDate() + 1);
        }
        return {
            start: start ? start.getTime() : null,
            end: end ? end.getTime() : null
        };
    }

    $scope.messageFilter = function (item) {
        var query = ($scope.searchText || '').toLowerCase().trim();
        var message = (item && item.Text ? item.Text : '').toLowerCase();
        if (query && message.indexOf(query) < 0) return false;
        var bounds = getRangeBounds();
        if (bounds.start || bounds.end) {
            var dt = parseMessageDateUtc(item);
            if (!dt) return false;
            var ms = dt.getTime();
            if (bounds.start && ms < bounds.start) return false;
            if (bounds.end && ms >= bounds.end) return false;
        }
        return true;
    };

    $scope.applyMessageFilter = function () {
        var list = $scope.items || [];
        $scope.filteredItems = list.filter($scope.messageFilter);
        $scope.totalMessages = $scope.filteredItems.length;
        $scope.currentPage = 1;
        updateCharts();
    };

    $scope.$watch('searchText', function () {
        $scope.applyMessageFilter();
    });

    $scope.$watchGroup(['periodStart', 'periodEnd'], function () {
        $scope.applyMessageFilter();
    });

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

    function normalizeSentimentValue(value) {
        var s = (value || '').toString().toLowerCase();
        if (s === 'positive') return 'positive';
        if (s === 'negative') return 'negative';
        if (s === 'neutral') return 'neutral';
        if (s === 'unknown') return 'unknown';
        return 'unknown';
    }

    function formatDay(date) {
        var d = date instanceof Date ? date : new Date(date);
        var year = d.getFullYear();
        var month = ('0' + (d.getMonth() + 1)).slice(-2);
        var day = ('0' + d.getDate()).slice(-2);
        return year + '-' + month + '-' + day;
    }

    function resolveChartDays() {
        var start = $scope.periodStart ? new Date($scope.periodStart) : null;
        var end = $scope.periodEnd ? new Date($scope.periodEnd) : null;
        if (!start || !end) {
            if (!$scope.filteredItems || $scope.filteredItems.length === 0) return [];
            var dates = $scope.filteredItems.map(function (item) {
                return item && item.DateText ? item.DateText.substring(0, 10) : null;
            }).filter(Boolean);
            if (dates.length === 0) return [];
            dates.sort();
            start = new Date(dates[0]);
            end = new Date(dates[dates.length - 1]);
        }
        start.setHours(0, 0, 0, 0);
        end.setHours(0, 0, 0, 0);
        if (end < start) {
            var tmp = start;
            start = end;
            end = tmp;
        }
        var days = [];
        var current = new Date(start.getTime());
        while (current <= end) {
            days.push(formatDay(current));
            current.setDate(current.getDate() + 1);
        }
        return days;
    }

    function buildPieChart(counts) {
        var total = sentimentOrder.reduce(function (sum, key) { return sum + (counts[key] || 0); }, 0);
        $scope.sentimentTotal = total;
        var width = 240;
        var height = 240;
        var cx = width / 2;
        var cy = height / 2;
        var radius = 100;
        var startAngle = -Math.PI / 2;
        var slices = [];
        var legend = [];
        sentimentOrder.forEach(function (key) {
            var count = counts[key] || 0;
            var meta = sentimentMeta[key];
            if (count > 0 && total > 0) {
                var angle = (count / total) * Math.PI * 2;
                var endAngle = startAngle + angle;
                var x1 = cx + radius * Math.cos(startAngle);
                var y1 = cy + radius * Math.sin(startAngle);
                var x2 = cx + radius * Math.cos(endAngle);
                var y2 = cy + radius * Math.sin(endAngle);
                var largeArc = angle > Math.PI ? 1 : 0;
                var path = 'M ' + cx + ' ' + cy + ' L ' + x1 + ' ' + y1 + ' A ' + radius + ' ' + radius + ' 0 ' + largeArc + ' 1 ' + x2 + ' ' + y2 + ' Z';
                slices.push({ path: path, color: meta.color, count: count });
                startAngle = endAngle;
            }
            legend.push({ label: meta.label, color: meta.color, count: count });
        });
        $scope.pieChart = { width: width, height: height, slices: slices, legend: legend };
    }

    function buildBarChart(countsByDay, days) {
        var barWidth = 18;
        var barGap = 6;
        var chartHeight = 200;
        var padding = 20;
        var maxTotal = 0;
        days.forEach(function (day) {
            var c = countsByDay[day];
            if (!c) return;
            var total = sentimentOrder.reduce(function (sum, key) { return sum + (c[key] || 0); }, 0);
            if (total > maxTotal) maxTotal = total;
        });
        if (maxTotal === 0) maxTotal = 1;
        var isPercent = $scope.chartType === 'percent';
        if (isPercent) {
            maxTotal = 1;
        }
        var width = padding * 2 + days.length * (barWidth + barGap);
        var height = chartHeight + 40;
        var segments = [];
        var labels = [];
        var lineSeries = {
            total: [],
            positive: [],
            neutral: [],
            negative: [],
            unknown: []
        };
        var labelEvery = Math.max(1, Math.ceil(days.length / 10));
        days.forEach(function (day, index) {
            var counts = countsByDay[day] || {};
            var x = padding + index * (barWidth + barGap);
            var stackHeight = 0;
            var stackOrder = ['negative', 'neutral', 'positive', 'unknown'];
            var dayTotal = sentimentOrder.reduce(function (sum, key) { return sum + (counts[key] || 0); }, 0);
            if ($scope.chartType === 'line') {
                var totalValue = dayTotal / maxTotal;
                var totalHeight = totalValue * chartHeight;
                var totalY = chartHeight - totalHeight + padding;
                lineSeries.total.push({ x: x + barWidth / 2, y: totalY });
                sentimentOrder.forEach(function (key) {
                    var value = counts[key] || 0;
                    var lineValue = value / maxTotal;
                    var lineHeight = lineValue * chartHeight;
                    var lineY = chartHeight - lineHeight + padding;
                    lineSeries[key].push({ x: x + barWidth / 2, y: lineY });
                });
            } else {
                var denom = isPercent ? (dayTotal || 1) : maxTotal;
                stackOrder.forEach(function (key) {
                    var value = counts[key] || 0;
                    if (value <= 0) return;
                    var heightValue = (value / denom) * chartHeight;
                    var y = chartHeight - stackHeight - heightValue + padding;
                    segments.push({
                        x: x,
                        y: y,
                        width: barWidth,
                        height: heightValue,
                        color: sentimentMeta[key].color
                    });
                    stackHeight += heightValue;
                });
            }
            if (index % labelEvery === 0) {
                labels.push({ x: x + barWidth / 2, y: chartHeight + padding + 14, text: day.substring(5) });
            }
        });
        var linePaths = [];
        if ($scope.chartType === 'line') {
            var lineKeys = ['total'].concat(sentimentOrder);
            lineKeys.forEach(function (key) {
                var points = lineSeries[key] || [];
                if (points.length === 0) return;
                var path = 'M ' + points[0].x + ' ' + points[0].y;
                for (var i = 1; i < points.length; i++) {
                    path += ' L ' + points[i].x + ' ' + points[i].y;
                }
                var color = key === 'total' ? '#444' : sentimentMeta[key].color;
                var label = key === 'total' ? 'Все' : sentimentMeta[key].label;
                linePaths.push({ path: path, color: color, label: label });
            });
        }
        $scope.barChart = { width: width, height: height, days: days, segments: segments, labels: labels, linePaths: linePaths };
    }

    function updateCharts() {
        var counts = { positive: 0, neutral: 0, negative: 0, unknown: 0 };
        var countsByDay = {};
        var allMap = {};
        var positiveMap = {};
        var negativeMap = {};
        var items = $scope.filteredItems || [];
        for (var i = 0; i < items.length; i++) {
            var item = items[i];
            var sentimentKey = normalizeSentimentValue(item && item.Sentiment);
            counts[sentimentKey] = (counts[sentimentKey] || 0) + 1;
            var sender = (item && item.Sender ? item.Sender : '').trim();
            if (sender) {
                allMap[sender] = (allMap[sender] || 0) + 1;
                if (sentimentKey === 'positive') {
                    positiveMap[sender] = (positiveMap[sender] || 0) + 1;
                }
                if (sentimentKey === 'negative') {
                    negativeMap[sender] = (negativeMap[sender] || 0) + 1;
                }
            }
            var dayKey = item && item.DateText ? item.DateText.substring(0, 10) : null;
            if (dayKey) {
                if (!countsByDay[dayKey]) {
                    countsByDay[dayKey] = { positive: 0, neutral: 0, negative: 0, unknown: 0 };
                }
                countsByDay[dayKey][sentimentKey] = (countsByDay[dayKey][sentimentKey] || 0) + 1;
            }
        }
        buildPieChart(counts);
        var days = resolveChartDays();
        buildBarChart(countsByDay, days);
        $scope.topAllCommenters = buildTopList(allMap, items.length);
        $scope.topPositiveCommenters = buildTopList(positiveMap, items.length);
        $scope.topNegativeCommenters = buildTopList(negativeMap, items.length);
    }

    function buildTopList(map, total) {
        var list = [];
        for (var key in map) {
            if (!map.hasOwnProperty(key)) continue;
            var count = map[key];
            var percent = total > 0 ? (count / total * 100) : 0;
            list.push({ name: key, count: count, percent: percent });
        }
        list.sort(function (a, b) {
            if (b.count === a.count) return a.name.localeCompare(b.name);
            return b.count - a.count;
        });
        return list.slice(0, 20);
    }

    $scope.sentimentLabel = function (item) {
        if (!item || !item.Sentiment) return '';
        switch (item.Sentiment) {
            case 'positive': return 'Позитивное';
            case 'negative': return 'Негативное';
            case 'neutral': return 'Нейтральное';
            case 'unknown': return 'Не определено';
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
        if ($scope.periodEnd) {
            var endDate = new Date($scope.periodEnd);
            endDate.setDate(endDate.getDate() + 1);
            qs.push('end=' + encodeURIComponent(formatDate(endDate)));
        }
        var query = ($scope.searchText || '').trim();
        if (query) qs.push('q=' + encodeURIComponent(query));
        if (qs.length > 0) url += '&' + qs.join('&');
        $http.get(url).then(function (r) {
            $scope.items = r.data || [];
            $scope.applyMessageFilter();
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
        start.setMonth(start.getMonth() - 2);
        $scope.periodStart = start;
        $scope.periodEnd = now;
        $scope.activeRange = '2months';
        $scope.loadTitle();
        $scope.refresh();
    }
}]);
