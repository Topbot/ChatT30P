angular.module('stats').controller('ChatAccountsController', ["$scope", "$http", function ($scope, $http) {
    $scope.items = [];
    $scope.phone = "";

    $scope.load = function () {
        $http.get('/api/ChatAccounts').then(function (r) {
            $scope.items = r.data || [];
        }, function () {
            $scope.items = [];
        });
    };

    $scope.addTelegram = function () {
        if (!$scope.phone || !$scope.phone.trim()) {
            return;
        }

        var payload = {
            Platform: 'Telegram',
            Phone: $scope.phone.trim(),
            Status: 0
        };

        $http.post('/api/ChatAccounts', payload).then(function () {
            $scope.phone = "";
            $scope.load();
        });
    };

    $scope.remove = function (item) {
        if (!item) return;
        var url = '/api/ChatAccounts?platform=' + encodeURIComponent(item.Platform || item.platform || '') + '&phone=' + encodeURIComponent(item.Phone || item.phone || '');
        $http.delete(url).then(function () {
            $scope.load();
        });
    };

    $scope.load();
}]);
